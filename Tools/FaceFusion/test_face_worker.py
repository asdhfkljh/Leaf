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



class PortraitTests(unittest.TestCase):
    def setUp(self):
        from face_worker import portrait
        self.portrait = portrait
        self.points = [SimpleNamespace(x=.5,y=.5) for _ in range(478)]
        indices = [10,338,297,332,284,251,389,356,454,323,361,288,397,365,379,378,400,377,152,148,176,149,150,136,172,58,132,93,234,127,162,21,54,103,67,109]
        for i,index in enumerate(indices):
            angle = 2*np.pi*i/len(indices)
            self.points[index] = SimpleNamespace(x=.5+.22*np.sin(angle),y=.5-.36*np.cos(angle))
        self.points[33] = SimpleNamespace(x=.35,y=.38)
        self.points[263] = SimpleNamespace(x=.65,y=.38)
        self.frame = np.full((480,640,3), 120, np.uint8)

    def test_mask_and_talking(self):
        self.points[13].y=.6; self.points[14].y=.75
        image = self.portrait(self.points,self.frame,np,cv2)
        decoded = cv2.imdecode(np.frombuffer(base64.b64decode(image),np.uint8),cv2.IMREAD_UNCHANGED)
        self.assertEqual(decoded.shape,(512,384,4))
        self.assertEqual(int(decoded[0,0,3]),0)
        self.assertEqual(int(decoded[256,192,3]),255)
        self.assertTrue(np.any((decoded[:,:,3]>0)&(decoded[:,:,3]<255)))

    def test_clipped_or_invalid_rejected(self):
        self.points[10].y=-.1
        self.assertIsNone(self.portrait(self.points,self.frame,np,cv2))
        self.points[10].y=float('nan')
        self.assertIsNone(self.portrait(self.points,self.frame,np,cv2))

class SkinColourTests(unittest.TestCase):
    def setUp(self):
        fixture = ProportionTests(); fixture.setUp(); self.p = fixture.p

    def test_rgb_order_and_dark_skin(self):
        from face_worker import sample_skin
        for bgr in [(80,120,180),(18,30,45)]:
            frame = np.full((480,640,3),bgr,np.uint8)
            np.testing.assert_allclose(sample_skin(self.p,frame,np),np.array(bgr[::-1])/255)

    def test_cheeks_ignore_eyes_lips_and_highlight_outliers(self):
        from face_worker import sample_skin
        frame = np.full((480,640,3),(80,120,180),np.uint8)
        frame[140:200,:]=0  # eyes / glasses
        frame[300:340,:]=(0,0,255)  # lips
        frame[265:267,210:230]=255  # small cheek highlight
        np.testing.assert_allclose(sample_skin(self.p,frame,np),[180/255,120/255,80/255])

    def test_invalid_geometry_has_no_colour(self):
        from face_worker import sample_skin
        frame = np.zeros((480,640,3),np.uint8)
        self.p[1].x=float('nan')
        self.assertIsNone(sample_skin(self.p,frame,np))


class PointCloudTests(unittest.TestCase):
    def test_flattened_landmarks_keep_depth_and_rgb_order(self):
        from face_worker import point_cloud_data
        points = [SimpleNamespace(x=.25, y=.5, z=-.1) for _ in range(478)]
        frame = np.zeros((20, 40, 3), np.uint8)
        frame[10, 10] = (30, 80, 200)  # BGR input frame
        geometry, colours = point_cloud_data(points, frame, np)
        self.assertEqual(len(geometry), 478 * 3)
        self.assertEqual(len(colours), 478 * 3)
        np.testing.assert_allclose(geometry[:3], [.25, .5, -.1])
        np.testing.assert_allclose(colours[:3], [200 / 255, 80 / 255, 30 / 255], atol=1e-5)

if __name__=='__main__': unittest.main()
