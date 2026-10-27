"""CPU 3DDFA_V2 inference using official ONNX weights and BFM identity bases.

Preprocessing/62-parameter layout follow cleardusk/3DDFA_V2 (MIT, pinned in prepare script).
MediaPipe supplies face localization only; all returned identity geometry comes from 3DDFA.
"""
from pathlib import Path
import time
import cv2
import numpy as np
import onnxruntime as ort

DATA = Path(__file__).with_name('3ddfa-data')


def crop_square(image, roi):
    x0, y0, x1, y1 = [int(round(v)) for v in roi]
    if x1 <= x0 or y1 <= y0 or max(x1-x0, y1-y0) > max(image.shape[:2])*4:
        raise ValueError('Invalid face ROI')
    result = np.zeros((y1-y0, x1-x0, 3), dtype=np.uint8)
    sx, sy = max(0,x0), max(0,y0)
    ex, ey = min(image.shape[1],x1), min(image.shape[0],y1)
    if ex <= sx or ey <= sy:
        raise ValueError('Face ROI is outside image')
    result[sy-y0:ey-y0, sx-x0:ex-x0] = image[sy:ey,sx:ex]
    return result


class TDDFAEngine:
    def __init__(self, data=DATA):
        self.data = Path(data)
        with np.load(self.data/'runtime.npz', allow_pickle=False) as stored:
            self.mean=stored['mean'].copy(); self.std=stored['std'].copy()
            self.u=stored['u'].reshape(-1).copy(); self.basis=stored['basis'].copy()
            self.expression=stored['expression'].copy(); self.keypoints=stored['keypoints'].copy()
            self.model_id=str(stored['model_id'])
        options=ort.SessionOptions();options.intra_op_num_threads=2;options.inter_op_num_threads=1
        self.session=ort.InferenceSession(str(self.data/'mobilenet.onnx'), sess_options=options, providers=['CPUExecutionProvider'])
        self.input_name=self.session.get_inputs()[0].name
        self._forward(np.zeros((120,120,3),dtype=np.uint8),[0,0,120,120])

    def _forward(self,image,roi):
        crop=cv2.resize(crop_square(image,roi),(120,120),interpolation=cv2.INTER_LINEAR)
        tensor=(crop.astype(np.float32).transpose(2,0,1)[None]-127.5)/128
        raw=self.session.run(None,{self.input_name:tensor})[0].reshape(-1)
        if raw.shape!=(62,) or not np.isfinite(raw).all():
            raise ValueError('Invalid 3DDFA prediction')
        return raw*self.std+self.mean

    def projected_landmarks(self,param,roi):
        affine=param[:12].reshape(3,4)
        vertices=(self.u[self.keypoints]+self.basis[self.keypoints]@param[12:52]+
                  self.expression[self.keypoints]@param[52:62]).reshape(-1,3).T
        projected=affine[:,:3]@vertices+affine[:,3:4]
        projected[0]-=1;projected[1]=120-projected[1]
        projected[0]=projected[0]*(roi[2]-roi[0])/120+roi[0]
        projected[1]=projected[1]*(roi[3]-roi[1])/120+roi[1]
        return projected[:2].T

    def infer(self,image,landmarks):
        start=time.perf_counter()
        h,w=image.shape[:2]
        p=np.array([(v.x*w,v.y*h) for v in landmarks],dtype=np.float32)
        if p.shape!=(478,2) or not np.isfinite(p).all(): return None
        eye_span=float(np.linalg.norm(p[33]-p[263]));mid=(p[33]+p[263])/2
        if eye_span<65 or abs(float(p[1,0]-mid[0]))>eye_span*.22 or abs(float(p[33,1]-p[263,1]))>eye_span*.22:
            return None
        contour=p[[10,152,234,454]]
        if (contour[:,0]<.01*w).any() or (contour[:,0]>.99*w).any() or (contour[:,1]<.01*h).any() or (contour[:,1]>.99*h).any():
            return None
        left,top=p.min(0);right,bottom=p.max(0)
        old_size=float((right-left+bottom-top)/2)
        cx=float((left+right)/2);cy=float((top+bottom)/2)+old_size*.14
        size=int(old_size*1.58);roi=[cx-size/2,cy-size/2,cx+size/2,cy+size/2]
        param=self._forward(image,roi)
        projected=self.projected_landmarks(param,roi)
        # The second pass uses 3DDFA's own 68-point projection for its training crop convention.
        lo,hi=projected.min(0),projected.max(0);center=(lo+hi)/2
        length=float(np.max(hi-lo)*np.sqrt(2))
        roi=[float(center[0]-length/2),float(center[1]-length/2),float(center[0]+length/2),float(center[1]+length/2)]
        param=self._forward(image,roi)
        projected=self.projected_landmarks(param,roi)
        if not np.isfinite(projected).all() or np.max(np.ptp(projected,axis=0))>max(w,h)*1.5:
            return None
        shape=param[12:52]/self.std[12:52]
        if not np.isfinite(shape).all() or np.max(np.abs(shape))>8:
            return None
        return {'shape_params':np.clip(shape,-3,3).astype(float).tolist(),'model_id':self.model_id,
                'inference_ms':round((time.perf_counter()-start)*1000,2)}

    def neutral_vertices(self, coefficients):
        """Identity-only geometry; never apply the estimated expression or camera transform to the avatar."""
        coefficients=np.asarray(coefficients,dtype=np.float32)
        if coefficients.shape!=(40,) or not np.isfinite(coefficients).all():
            raise ValueError('Expected 40 finite identity coefficients')
        return (self.u+np.einsum('ij,j->i',self.basis,coefficients*self.std[12:52],optimize=False)).reshape(-1,3)
