import base64
import json
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
import cv2
import numpy as np
from face_worker import measure


class ProportionTests(unittest.TestCase):
    def setUp(self):
        self.p = [SimpleNamespace(x=.5, y=.5) for _ in range(478)]
        for index, xy in {234:(.2,.5),454:(.8,.5),10:(.5,.15),152:(.5,.85),
                          33:(.30,.38),133:(.40,.38),263:(.70,.38),362:(.60,.38),
                          159:(.35,.36),145:(.35,.40),386:(.65,.36),374:(.65,.40),
                          1:(.5,.53),61:(.38,.65),291:(.62,.65),13:(.5,.645),14:(.5,.655)}.items():
            self.p[index] = SimpleNamespace(x=xy[0], y=xy[1])

    def test_expected_ratios(self):
        result = measure(self.p,640,480,np)
        self.assertAlmostEqual(result['eye_spacing_ratio'], .5)
        self.assertAlmostEqual(result['mouth_width_ratio'], .4)
        self.assertNotIn('atlas', result)

    def test_camera_distance_and_translation_invariance(self):
        expected = measure(self.p,640,480,np)
        for point in self.p:
            point.x = (point.x-.5)*.8+.52
            point.y = (point.y-.5)*.8+.48
        actual = measure(self.p,640,480,np)
        self.assertAlmostEqual(actual['eye_spacing_ratio'],expected['eye_spacing_ratio'])
        self.assertAlmostEqual(actual['mouth_width_ratio'],expected['mouth_width_ratio'])

    def test_small_roll_uses_euclidean_distances(self):
        expected = measure(self.p,640,480,np)
        angle=.05
        rotation=np.array([[np.cos(angle),-np.sin(angle)],[np.sin(angle),np.cos(angle)]])
        for p in self.p:
            q=rotation@np.array([(p.x-.5)*640,(p.y-.5)*480])
            p.x=q[0]/640+.5; p.y=q[1]/480+.5
        actual=measure(self.p,640,480,np)
        self.assertAlmostEqual(actual['eye_spacing_ratio'],expected['eye_spacing_ratio'])
        self.assertAlmostEqual(actual['mouth_width_ratio'],expected['mouth_width_ratio'])

    def test_yaw_rejected(self):
        self.p[1].x=.8
        self.assertIsNone(measure(self.p,640,480,np))

    def test_open_mouth_rejected(self):
        self.p[14].y=.75
        self.assertIsNone(measure(self.p,640,480,np))

    def test_blink_rejected(self):
        self.p[145].y=self.p[159].y
        self.assertIsNone(measure(self.p,640,480,np))

    def test_clipped_face_rejected(self):
        self.p[234].x=0
        self.assertIsNone(measure(self.p,640,480,np))

    def test_non_finite_rejected(self):
        self.p[1].x=float('nan')
        self.assertIsNone(measure(self.p,640,480,np))

    def test_small_face_rejected(self):
        self.assertIsNone(measure(self.p,100,100,np))


class WorkerTests(unittest.TestCase):
    def test_model_no_face_and_bad_frame_recovery(self):
        ok,jpeg=cv2.imencode('.jpg',np.zeros((480,640,3),dtype=np.uint8));self.assertTrue(ok)
        packet=json.dumps({'id':9,'image':base64.b64encode(jpeg).decode(),'rotation':90})
        result=subprocess.run([sys.executable,str(Path(__file__).with_name('face_worker.py'))],
                              input='{"id":8,"image":"bad"}\n'+packet+'\n',text=True,capture_output=True,timeout=60)
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        replies=[json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(replies[0]['type'],'ready')
        self.assertEqual(replies[1]['type'],'error')
        self.assertEqual(replies[2],{'type':'face','id':9,'valid':False})


if __name__=='__main__': unittest.main()
