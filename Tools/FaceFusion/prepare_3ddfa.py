"""Prepare official 3DDFA_V2/BFM assets locally. BFM data is academic-use only; not redistributed."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import pickle
import shutil
import struct
import urllib.request
import numpy as np

COMMIT = '1b6c67601abffc1e9f248b291708aef0e43b55ae'
RAW = f'https://raw.githubusercontent.com/cleardusk/3DDFA_V2/{COMMIT}/'
ONNX_URL = 'https://drive.usercontent.google.com/download?id=1YpO1KfXvJHRmCBkErNa62dHm-CUjsoIk&export=download&confirm=t'
EXPECTED_ONNX_SHA256 = '1c0a8acd50db28987773324a9b2b816361468e3aa13cb6b212c911b889e08c3e'
DATA = Path(__file__).with_name('3ddfa-data')


def download(url, destination):
    if destination.exists():
        return
    print('Downloading', destination.name, flush=True)
    temporary = destination.with_suffix(destination.suffix + '.download')
    with urllib.request.urlopen(urllib.request.Request(url, headers={'Range': 'bytes=0-0'}), timeout=60) as response:
        content_range = response.headers.get('Content-Range', '')
        if response.status == 206 and content_range:
            size = int(content_range.split('/')[-1])
        else:
            size = 0
            with temporary.open('wb') as out:
                shutil.copyfileobj(response, out)
    if size:
        parts = min(12, max(1, size // 1000000))
        def part(i):
            start, end = i * size // parts, (i + 1) * size // parts - 1
            request = urllib.request.Request(url, headers={'Range': f'bytes={start}-{end}'})
            with urllib.request.urlopen(request, timeout=120) as response:
                if response.status != 206:
                    raise RuntimeError('Server did not honor byte range')
                body = response.read()
            if len(body) != end - start + 1:
                raise RuntimeError('Incomplete model download')
            return body
        with concurrent.futures.ThreadPoolExecutor(max_workers=parts) as pool, temporary.open('wb') as out:
            for body in pool.map(part, range(parts)):
                out.write(body)
    temporary.replace(destination)


def prepare(data):
    # Only deserialize the files downloaded from the pinned official repository.
    with (data / 'bfm.pkl').open('rb') as stream:
        bfm = pickle.load(stream, encoding='latin1')
    with (data / 'params.pkl').open('rb') as stream:
        params = pickle.load(stream, encoding='latin1')
    with (data / 'tri.pkl').open('rb') as stream:
        triangles = np.asarray(pickle.load(stream, encoding='latin1'), dtype=np.int32).T
    u = np.asarray(bfm['u'], dtype=np.float32).reshape(-1, 3)
    basis = np.asarray(bfm['w_shp'], dtype=np.float32)[:, :40]
    expression = np.asarray(bfm['w_exp'], dtype=np.float32)[:, :10]
    mean = np.asarray(params['mean'], dtype=np.float32).ravel()
    std = np.asarray(params['std'], dtype=np.float32).ravel()
    indices = np.asarray(bfm['keypoints'], dtype=np.int64).ravel()
    if basis.shape != (u.size, 40) or mean.size != 62 or std.size != 62 or (std <= 0).any():
        raise ValueError('Unexpected upstream BFM dimensions')
    if triangles.ndim != 2 or triangles.shape[1] != 3 or triangles.min() < 0 or triangles.max() >= len(u):
        raise ValueError('Invalid upstream BFM topology')
    # The template is neutral identity only: expression and camera/pose are deliberately excluded.
    center = (u.min(axis=0) + u.max(axis=0)) / 2
    scale = float(np.ptp(u[:, 1]))
    transform = np.array([-1, 1, 1], dtype=np.float32) / scale
    vertices = (u - center) * transform
    frames = (basis * (std[12:52] * 3)[None, :]).T.reshape(40, len(u), 3) * transform
    triangles = triangles.copy()
    normals = np.cross(vertices[triangles[:, 1]] - vertices[triangles[:, 0]], vertices[triangles[:, 2]] - vertices[triangles[:, 0]])
    if float(normals[:, 2].sum()) < 0:
        triangles = triangles[:, [0, 2, 1]]
    model_id = hashlib.sha256(vertices.tobytes() + frames.tobytes()).digest()
    with (data / 'unity_face.bytes').open('wb') as out:
        out.write(b'L3DFACE1' + struct.pack('<iii', len(u), triangles.size, 40) + model_id)
        out.write(vertices.astype('<f4').tobytes())
        out.write(triangles.astype('<i4').tobytes())
        out.write(frames.astype('<f4').tobytes())
    np.savez(data / 'runtime.npz', u=u, basis=basis, expression=expression, mean=mean, std=std,
             keypoints=indices, model_id=np.array(model_id.hex()))
    check_shape = np.linspace(-.6, .6, 40, dtype=np.float32)
    check_vertices = u.reshape(-1) + np.einsum('ij,j->i', basis, check_shape * std[12:52], optimize=False)
    check_vertices = (check_vertices.reshape(-1, 3) - center) * transform
    (data / 'validation_shape.txt').write_text('\n'.join(str(float(v)) for v in check_shape) + '\n')
    check_vertices.astype('<f4').tofile(data / 'validation_face.bytes')
    manifest = {'upstream_commit': COMMIT, 'model_id': model_id.hex(), 'vertices': len(u),
                'triangles': len(triangles), 'identity_parameters': 40, 'license': 'BFM: academic use only',
                'onnx_sha256': hashlib.sha256((data / 'mobilenet.onnx').read_bytes()).hexdigest()}
    (data / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--onnx', type=Path, help='Optional previously downloaded official mb1_120x120.onnx')
    parser.add_argument('--source', type=Path, help='Optional official checkout to copy BFM config files from')
    args = parser.parse_args()
    DATA.mkdir(exist_ok=True)
    for local, remote in [('bfm.pkl', 'configs/bfm_noneck_v3.pkl'), ('params.pkl', 'configs/param_mean_std_62d_120x120.pkl'),
                          ('tri.pkl', 'configs/tri.pkl'), ('BFM-LICENSE.txt', 'bfm/readme.md'), ('CODE-LICENSE.txt', 'LICENSE')]:
        if args.source and (args.source / remote).exists():
            shutil.copyfile(args.source / remote, DATA / local)
        else:
            download(RAW + remote, DATA / local)
    if args.onnx:
        shutil.copyfile(args.onnx, DATA / 'mobilenet.onnx')
    else:
        download(ONNX_URL, DATA / 'mobilenet.onnx')
    if hashlib.sha256((DATA / 'mobilenet.onnx').read_bytes()).hexdigest() != EXPECTED_ONNX_SHA256:
        raise ValueError('ONNX checksum differs from the verified official mb1_120x120 model')
    import onnxruntime as ort
    session = ort.InferenceSession(str(DATA / 'mobilenet.onnx'), providers=['CPUExecutionProvider'])
    if session.get_inputs()[0].shape != [1, 3, 120, 120]:
        raise ValueError('Unexpected official ONNX input shape')
    prepare(DATA)


if __name__ == '__main__': main()
