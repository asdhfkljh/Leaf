import base64
import json
from pathlib import Path
import struct
import subprocess
import sys
import unittest
import cv2
import numpy as np
from tddfa_backend import crop_square, TDDFAEngine, DATA


class CropTests(unittest.TestCase):
    def test_outside_crop_is_padded_without_stretching(self):
        image=np.full((10,10,3),200,dtype=np.uint8)
        crop=crop_square(image,[-5,-5,5,5])
        self.assertEqual(crop.shape,(10,10,3));self.assertTrue((crop[:5]==0).all())
        self.assertTrue((crop[5:,5:]==200).all())

    def test_invalid_roi_rejected(self):
        with self.assertRaises(ValueError):crop_square(np.zeros((10,10,3),dtype=np.uint8),[5,5,4,4])


@unittest.skipUnless((DATA/'runtime.npz').exists(), 'Run prepare_3ddfa.py to install licensed model assets')
class ModelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.engine=TDDFAEngine()

    def test_unity_template_reconstructs_same_geometry_as_python(self):
        body=(DATA/'unity_face.bytes').read_bytes()
        self.assertEqual(body[:8],b'L3DFACE1')
        count,indices,dimensions=struct.unpack_from('<iii',body,8)
        self.assertEqual(dimensions,40);self.assertEqual(body[20:52].hex(),self.engine.model_id)
        base=np.frombuffer(body,dtype='<f4',count=count*3,offset=52).reshape(count,3)
        frames=np.frombuffer(body,dtype='<f4',count=count*3*40,offset=52+count*12+indices*4).reshape(40,count,3)
        coefficients=np.linspace(-.6,.6,40,dtype=np.float32)
        reconstructed=base+np.einsum('k,kni->ni',coefficients/3,frames)
        u=self.engine.u.reshape(-1,3);center=(u.min(0)+u.max(0))/2;scale=np.ptp(u[:,1])
        reference=(self.engine.neutral_vertices(coefficients)-center)*np.array([-1,1,1])/scale
        self.assertLess(float(np.max(np.abs(reconstructed-reference))),1e-6)

    def test_identity_function_rejects_nonfinite_or_wrong_dimension(self):
        for invalid in [np.zeros(39),np.full(40,np.nan)]:
            with self.assertRaises(ValueError):self.engine.neutral_vertices(invalid)

    def test_3ddfa_worker_no_face_and_bad_image_recovery(self):
        _,jpeg=cv2.imencode('.jpg',np.zeros((480,640,3),dtype=np.uint8))
        packet=json.dumps({'id':5,'image':base64.b64encode(jpeg).decode()})
        result=subprocess.run([sys.executable,str(Path(__file__).with_name('face_worker.py')),'--backend','3ddfa'],
                              input='{"id":4,"image":"bad"}\n'+packet+'\n',capture_output=True,text=True,timeout=90)
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        replies=[json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(replies[0]['backend'],'3ddfa');self.assertEqual(replies[0]['model_id'],self.engine.model_id)
        self.assertEqual(replies[1]['type'],'error');self.assertEqual(replies[2],{'type':'face','id':5,'valid':False})
