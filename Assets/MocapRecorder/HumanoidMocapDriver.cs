using TrackerPro;
using TrackerPro.Unity;
using UnityEngine;
// TrackerPro defines its own Screen / Color / Rect types; make sure we use Unity's.
using Screen = UnityEngine.Screen;
using Color = UnityEngine.Color;
using Rect = UnityEngine.Rect;

namespace MocapRecording
{
    /// <summary>
    /// Drives ANY Humanoid character (Mixamo, Ready Player Me, VRoid, Unity humanoids...) from the
    /// TrackerPro / MediaPipe body landmarks. Replaces FullBodyAvatarMocap / AvatarMocap:
    ///  - no Skeleton Parent needed: it reads the tracker directly every frame,
    ///  - works with the Holistic (FullBodyAvatarMocap scene) or Pose tracker,
    ///  - every rotation is measured from the character's own rest pose, so bone axis
    ///    conventions, T-pose vs A-pose, scale and which way the character faces don't matter.
    /// Requirement: the model's FBX must be imported with Rig > Animation Type = Humanoid.
    /// </summary>
    [DefaultExecutionOrder(9000)] // after the Animator, before HumanoidMocapRecorder (10000)
    [RequireComponent(typeof(Animator))]
    public class HumanoidMocapDriver : MonoBehaviour
    {
        [Tooltip("CoreHolisticTrackingSolution or CorePoseTrackingSolution. Leave empty to find it automatically.")]
        public MonoBehaviour tracker;

        [Header("Tracking")]
        [Tooltip("Landmarks less visible than this keep their last good position.")]
        [Range(0f, 1f)] public float visibilityThreshold = 0.5f;
        [Tooltip("0 = raw (jittery), 0.9 = very smooth (laggy).")]
        [Range(0f, 0.95f)] public float smoothing = 0.5f;
        [Tooltip("Tick if the character raises its LEFT arm when you raise your RIGHT arm and you don't want that (or vice-versa).")]
        public bool mirror = false;

        [Header("What to drive")]
        public bool driveHead = true;
        [Range(0f, 45f)] public float headPitchOffset = 15f;
        [Tooltip("Largest angle the head may turn away from the chest (stops 90° snaps when tracking is noisy).")]
        [Range(20f, 90f)] public float maxHeadAngle = 60f;
        public bool driveHands = true;
        public bool driveFeet = true;
        [Tooltip("Uses the Holistic tracker's 21 hand points per hand (FullBodyAvatarMocap scene). Needs a rig with finger bones.")]
        public bool driveFingers = true;
        [Range(0f, 0.95f)] public float fingerSmoothing = 0.6f;
        [Tooltip("Moves the hips down when the knees bend so the feet stay on the floor (squats, crouches).")]
        public bool keepFeetOnGround = true;

        [Header("Root motion (experimental)")]
        [Tooltip("Estimate where you are in the room from the webcam image, so walking / side-steps / jumps move the character. " +
                 "Untick 'In Place' on the recorder to keep it in the clip.")]
        public bool rootMotion = false;
        [Tooltip("Sideways movement and walking towards / away from the camera.")]
        public bool rootHorizontal = true;
        [Tooltip("Jumps (needs your ankles visible).")]
        public bool rootVertical = true;
        [Tooltip("Your webcam's vertical field of view. Most webcams are 40-50°. Wrong values only scale the distances.")]
        [Range(20f, 90f)] public float cameraVerticalFov = 45f;
        [Tooltip("Distance from the camera is noisy, so it's smoothed more than the pose.")]
        [Range(0f, 0.98f)] public float rootSmoothing = 0.85f;

        [Header("Debug")]
        public bool showStatus = true;
        [Tooltip("Shows the webcam image with the tracked body and hand points in a corner of the Game view.")]
        public bool showDebugView = true;
        [Range(0.15f, 0.6f)] public float debugViewSize = 0.3f;
        [Tooltip("Flip the debug drawing if the dots don't line up with you on the webcam image.")]
        public bool debugFlipX, debugFlipY;

        // MediaPipe pose landmark indices (person's own left/right)
        const int NOSE = 0, L_EAR = 7, R_EAR = 8, L_SH = 11, R_SH = 12, L_EL = 13, R_EL = 14,
                  L_WR = 15, R_WR = 16, L_PINKY = 17, R_PINKY = 18, L_INDEX = 19, R_INDEX = 20,
                  L_HIP = 23, R_HIP = 24, L_KNEE = 25, R_KNEE = 26, L_ANK = 27, R_ANK = 28,
                  L_FOOT = 31, R_FOOT = 32, COUNT = 33;

        class Limb
        {
            public HumanBodyBones upper, lower, end;
            public int a, b, c;            // landmarks: e.g. shoulder, elbow, wrist
            public bool isLeg;
            public Vector3 restUpperDir, restLowerDir, restSec, prevSec;
            public bool valid;
        }

        class EndBone
        {
            public HumanBodyBones bone;
            public Vector3 restPrimary, restSec;
            public bool valid;
        }

        // MediaPipe hand landmarks: 0 wrist; thumb 1-4; index 5-8; middle 9-12; ring 13-16; little 17-20
        static readonly int[][] FingerLandmarks =
        {
            new[] { 1, 2, 3, 4 }, new[] { 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 }, new[] { 13, 14, 15, 16 }, new[] { 17, 18, 19, 20 }
        };
        static readonly HumanBodyBones[][] LeftFingerBones =
        {
            new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal },
            new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
            new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
            new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
            new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
        };
        static readonly HumanBodyBones[][] RightFingerBones =
        {
            new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal },
            new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
        };

        class HandTrack
        {
            public readonly Vector3[] p = new Vector3[21];
            public bool seen;
            public object lastList;
            public float lastTime = -999f;
            public bool Fresh => Time.unscaledTime - lastTime < 0.3f;
        }
        readonly HandTrack _leftHandTrack = new HandTrack(), _rightHandTrack = new HandTrack();
        Vector3[] _alongLocal;   // each finger bone's "pointing" axis in its own local space
        bool[] _hasAlong;
        int _fingerBoneCount;

        Animator _animator;
        Transform _root;
        Transform[] _bones;
        Quaternion[] _restLocalRot;
        Vector3 _restHipsLocalPos;
        Quaternion[] _restCharRot;      // bone rotation in character (root) space at rest
        Quaternion _restHipBasisInv, _restTorsoBasisInv;
        float _restFootHeight;
        Limb[] _limbs;
        EndBone _lHand, _rHand, _lFoot, _rFoot;

        readonly Vector3[] _lm = new Vector3[COUNT];
        readonly bool[] _lmSeen = new bool[COUNT];
        object _lastList;
        float _lastDataTime = -999f, _nextTrackerSearch;
        bool _hasPose, _ready;
        float _groundOffset;
        bool _lowerBodyVisible = true;
        bool _rootCalibrated;
        Vector3 _rootOrigin, _rootTarget, _rootSmoothed;
        float _footBaseline = float.NaN, _personLegLength = 0.85f, _charLegLength = 1f;
        string _status = "";

        // ------------------------------------------------------------------ setup

        void Awake()
        {
            _animator = GetComponent<Animator>();
            _root = transform;

            // The old scripts fight over the same bones; switch them off on this character.
            foreach (var old in GetComponents<TrackingEventHandler>()) old.enabled = false;

            if (_animator.avatar == null || !_animator.avatar.isHuman)
            {
                _status = "Needs a Humanoid avatar: select the model FBX > Rig > Animation Type: Humanoid > Apply";
                Debug.LogError($"[MocapDriver] '{name}': {_status}", this);
                enabled = false;
                return;
            }
            if (_animator.runtimeAnimatorController != null)
                Debug.LogWarning($"[MocapDriver] '{name}' has an Animator Controller. Clear the Controller field " +
                                 "while recording so animations don't fight the mocap.", this);
            CaptureRestPose();
        }

        Vector3 CharPos(Transform t) => _root.InverseTransformPoint(t.position);
        Transform B(HumanBodyBones b) => _bones[(int)b];

        void CaptureRestPose()
        {
            int n = (int)HumanBodyBones.LastBone;
            _bones = new Transform[n];
            _restLocalRot = new Quaternion[n];
            _restCharRot = new Quaternion[n];
            _alongLocal = new Vector3[n];
            _hasAlong = new bool[n];
            var rootInv = Quaternion.Inverse(_root.rotation);
            for (int i = 0; i < n; i++)
            {
                var t = _animator.GetBoneTransform((HumanBodyBones)i);
                _bones[i] = t;
                if (t == null) continue;
                _restLocalRot[i] = t.localRotation;
                _restCharRot[i] = rootInv * t.rotation;
            }
            _restHipsLocalPos = B(HumanBodyBones.Hips).localPosition;

            // Rest "body frames" from the skeleton itself (works for T-pose and A-pose rigs)
            Vector3 lUL = CharPos(B(HumanBodyBones.LeftUpperLeg)), rUL = CharPos(B(HumanBodyBones.RightUpperLeg));
            Vector3 lUA = CharPos(B(HumanBodyBones.LeftUpperArm)), rUA = CharPos(B(HumanBodyBones.RightUpperArm));
            Vector3 up = (lUA + rUA) * 0.5f - (lUL + rUL) * 0.5f;
            _restHipBasisInv = Quaternion.Inverse(Basis(rUL - lUL, up, Quaternion.identity));
            _restTorsoBasisInv = Quaternion.Inverse(Basis(rUA - lUA, up, Quaternion.identity));

            _limbs = new[]
            {
                MakeLimb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, L_SH, L_EL, L_WR, false),
                MakeLimb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, R_SH, R_EL, R_WR, false),
                MakeLimb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, L_HIP, L_KNEE, L_ANK, true),
                MakeLimb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, R_HIP, R_KNEE, R_ANK, true),
            };
            _lHand = MakeHand(HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftLittleProximal);
            _rHand = MakeHand(HumanBodyBones.RightHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightLittleProximal);
            _lFoot = MakeFoot(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes);
            _rFoot = MakeFoot(HumanBodyBones.RightFoot, HumanBodyBones.RightToes);
            CaptureFingerRest(LeftFingerBones);
            CaptureFingerRest(RightFingerBones);
            _charLegLength =
                Vector3.Distance(CharPos(B(HumanBodyBones.LeftUpperLeg)), CharPos(B(HumanBodyBones.LeftLowerLeg))) +
                Vector3.Distance(CharPos(B(HumanBodyBones.LeftLowerLeg)), CharPos(B(HumanBodyBones.LeftFoot)));
            _restFootHeight = Mathf.Min(CharPos(B(HumanBodyBones.LeftFoot)).y, CharPos(B(HumanBodyBones.RightFoot)).y);
            _ready = true;
        }

        void CaptureFingerRest(HumanBodyBones[][] fingers)
        {
            foreach (var chain in fingers)
            {
                for (int k = 0; k < chain.Length; k++)
                {
                    var t = B(chain[k]);
                    if (t == null) break;
                    Vector3 dir;
                    var next = k + 1 < chain.Length ? B(chain[k + 1]) : null;
                    if (next != null) dir = next.position - t.position;                  // to next joint
                    else if (t.childCount > 0) dir = t.GetChild(0).position - t.position; // to fingertip end bone
                    else if (k > 0 && B(chain[k - 1]) != null) dir = t.position - B(chain[k - 1]).position;
                    else continue;
                    if (dir.sqrMagnitude < 1e-12f) continue;
                    _alongLocal[(int)chain[k]] = Quaternion.Inverse(t.rotation) * dir;
                    _hasAlong[(int)chain[k]] = true;
                    _fingerBoneCount++;
                }
            }
        }

        Limb MakeLimb(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end, int a, int b, int c, bool isLeg)
        {
            var l = new Limb { upper = upper, lower = lower, end = end, a = a, b = b, c = c, isLeg = isLeg };
            if (B(upper) == null || B(lower) == null || B(end) == null) return l;
            l.restUpperDir = CharPos(B(lower)) - CharPos(B(upper));
            l.restLowerDir = CharPos(B(end)) - CharPos(B(lower));
            // Hinge axis: elbows bend forward, knees bend backward
            l.restSec = Vector3.Cross(l.restUpperDir.normalized, isLeg ? Vector3.back : Vector3.forward).normalized;
            l.prevSec = l.restSec;
            l.valid = l.restSec.sqrMagnitude > 0.5f;
            return l;
        }

        EndBone MakeHand(HumanBodyBones hand, HumanBodyBones index, HumanBodyBones little)
        {
            var e = new EndBone { bone = hand };
            if (B(hand) == null || B(index) == null || B(little) == null) return e;
            Vector3 h = CharPos(B(hand)), i = CharPos(B(index)), l = CharPos(B(little));
            e.restPrimary = (i + l) * 0.5f - h;
            e.restSec = Vector3.Cross(i - h, l - h);
            e.valid = e.restPrimary.sqrMagnitude > 1e-8f && e.restSec.sqrMagnitude > 1e-10f;
            return e;
        }

        EndBone MakeFoot(HumanBodyBones foot, HumanBodyBones toes)
        {
            var e = new EndBone { bone = foot };
            if (B(foot) == null || B(toes) == null) return e;
            e.restPrimary = CharPos(B(toes)) - CharPos(B(foot));
            e.restSec = Vector3.Cross(e.restPrimary, Vector3.right);
            e.valid = e.restSec.sqrMagnitude > 1e-10f;
            return e;
        }

        // ------------------------------------------------------------------ tracker input

        LandmarkList ReadTracker()
        {
            if (tracker == null && Time.unscaledTime >= _nextTrackerSearch)
            {
                _nextTrackerSearch = Time.unscaledTime + 1f;
                tracker = (MonoBehaviour)FindAnyObjectByType<CoreHolisticTrackingSolution>()
                          ?? FindAnyObjectByType<CorePoseTrackingSolution>();
            }
            var holistic = tracker as CoreHolisticTrackingSolution;
            if (holistic != null) return holistic.poseWorldLandmarks;
            var pose = tracker as CorePoseTrackingSolution;
            if (pose != null) return pose.poseWorldLandmarks;
            return null;
        }

        void ReadHands()
        {
            var holistic = tracker as CoreHolisticTrackingSolution;
            if (holistic == null) return;
            ConsumeHand(_leftHandTrack, mirror ? holistic.rightHandLandmarks : holistic.leftHandLandmarks);
            ConsumeHand(_rightHandTrack, mirror ? holistic.leftHandLandmarks : holistic.rightHandLandmarks);
        }

        void ConsumeHand(HandTrack track, NormalizedLandmarkList list)
        {
            if (list == null || list.Landmark.Count < 21 || ReferenceEquals(list, track.lastList)) return;
            track.lastList = list;
            track.lastTime = Time.unscaledTime;

            // Hand points are normalised image coords: scale by the image size so x and y use the same units
            // (MediaPipe's z uses the same scale as x), then convert like the body points.
            float w = 1f, h = 1f;
            var source = ImageSourceProvider.ImageSource;
            if (source != null && source.textureWidth > 0 && source.textureHeight > 0)
            {
                w = source.textureWidth;
                h = source.textureHeight;
            }
            float k = 1f - fingerSmoothing;
            for (int i = 0; i < 21; i++)
            {
                var src = list.Landmark[i];
                var p = new Vector3((mirror ? src.X : -src.X) * w, -src.Y * h, -src.Z * w);
                track.p[i] = track.seen ? Vector3.Lerp(track.p[i], p, k) : p;
            }
            track.seen = true;
        }

        static int Mirror(int i)
        {
            if (i == 0) return 0;
            if (i <= 3) return i + 3;           // left eye points -> right eye points
            if (i <= 6) return i - 3;
            return (i % 2 == 1) ? i + 1 : i - 1; // 7/8, 9/10, 11/12 ... 31/32
        }

        void ConsumeLandmarks(LandmarkList list)
        {
            if (list == null || list.Landmark.Count < COUNT) return;
            float k = 1f - smoothing;
            for (int i = 0; i < COUNT; i++)
            {
                var src = list.Landmark[mirror ? Mirror(i) : i];
                // MediaPipe (x right, y down, z away from camera) -> character space
                // (+X = character's right, +Y = up, +Z = character's forward).
                var p = new Vector3(mirror ? src.X : -src.X, -src.Y, -src.Z);
                if (!_lmSeen[i]) { _lm[i] = p; _lmSeen[i] = true; continue; }
                if (src.Visibility < visibilityThreshold) continue; // hold last good value
                _lm[i] = Vector3.Lerp(_lm[i], p, k);
            }
            _hasPose = true;
            float lower = Mathf.Min(list.Landmark[L_HIP].Visibility, list.Landmark[R_HIP].Visibility);
            _lowerBodyVisible = lower >= visibilityThreshold;
        }

        // ------------------------------------------------------------------ solve

        void LateUpdate()
        {
            if (!_ready) return;
            var list = ReadTracker();
            if (list != null && !ReferenceEquals(list, _lastList))
            {
                _lastList = list;
                _lastDataTime = Time.unscaledTime;
                ConsumeLandmarks(list);
                if (rootMotion) EstimateRoot(list, ReadNormalizedPose());
            }
            if (driveFingers) ReadHands();
            UpdateStatus();
            if (!_hasPose) return;

            // 1. back to rest so nothing accumulates (fingers etc. stay in rest pose)
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null) _bones[i].localRotation = _restLocalRot[i];
            B(HumanBodyBones.Hips).localPosition = _restHipsLocalPos;

            // 2. torso frames
            Vector3 midHip = (_lm[L_HIP] + _lm[R_HIP]) * 0.5f;
            Vector3 midSh = (_lm[L_SH] + _lm[R_SH]) * 0.5f;
            Vector3 up = midSh - midHip;
            Quaternion hipDelta = Basis(_lm[R_HIP] - _lm[L_HIP], up, Quaternion.Inverse(_restHipBasisInv)) * _restHipBasisInv;
            Quaternion torsoDelta = Basis(_lm[R_SH] - _lm[L_SH], up, Quaternion.Inverse(_restTorsoBasisInv)) * _restTorsoBasisInv;

            SetCharRot(HumanBodyBones.Hips, hipDelta);
            SetCharRot(HumanBodyBones.Spine, Quaternion.Slerp(hipDelta, torsoDelta, 0.5f));
            SetCharRot(HumanBodyBones.Chest, torsoDelta);
            SetCharRot(HumanBodyBones.UpperChest, torsoDelta);
            SetCharRot(HumanBodyBones.LeftShoulder, torsoDelta);
            SetCharRot(HumanBodyBones.RightShoulder, torsoDelta);
            SetCharRot(HumanBodyBones.Neck, torsoDelta);

            if (driveHead)
            {
                Vector3 right = _lm[R_EAR] - _lm[L_EAR];
                Vector3 fwd = _lm[NOSE] - (_lm[L_EAR] + _lm[R_EAR]) * 0.5f;
                Vector3 headUp = Vector3.Cross(fwd, right);
                Quaternion head = Basis(right, headUp, torsoDelta) * Quaternion.Euler(-headPitchOffset, 0f, 0f);
                // Limit how far the head can turn relative to the chest (noisy face depth = sudden 90° turns).
                Quaternion rel = Quaternion.Inverse(torsoDelta) * head;
                float angle = Quaternion.Angle(Quaternion.identity, rel);
                if (angle > maxHeadAngle)
                    head = torsoDelta * Quaternion.Slerp(Quaternion.identity, rel, maxHeadAngle / angle);
                SetCharRot(HumanBodyBones.Neck, Quaternion.Slerp(torsoDelta, head, 0.4f));
                SetCharRot(HumanBodyBones.Head, head);
            }

            // 3. limbs (top-down so children inherit correctly)
            Vector3 torsoFwd = torsoDelta * Vector3.forward;
            Vector3 hipFwd = hipDelta * Vector3.forward;
            foreach (var l in _limbs)
                if (l.valid) SolveLimb(l, l.isLeg ? -hipFwd : torsoFwd);

            if (driveHands)
            {
                SolveEnd(_lHand, (_lm[L_INDEX] + _lm[L_PINKY]) * 0.5f - _lm[L_WR], Vector3.Cross(_lm[L_INDEX] - _lm[L_WR], _lm[L_PINKY] - _lm[L_WR]));
                SolveEnd(_rHand, (_lm[R_INDEX] + _lm[R_PINKY]) * 0.5f - _lm[R_WR], Vector3.Cross(_lm[R_INDEX] - _lm[R_WR], _lm[R_PINKY] - _lm[R_WR]));
            }
            if (driveFeet)
            {
                Vector3 hipRight = _lm[R_HIP] - _lm[L_HIP];
                Vector3 lf = _lm[L_FOOT] - _lm[L_ANK], rf = _lm[R_FOOT] - _lm[R_ANK];
                SolveEnd(_lFoot, lf, Vector3.Cross(lf, hipRight));
                SolveEnd(_rFoot, rf, Vector3.Cross(rf, hipRight));
            }

            if (driveFingers && _fingerBoneCount > 0)
            {
                SolveHandFromHandLandmarks(_lHand, _leftHandTrack, LeftFingerBones);
                SolveHandFromHandLandmarks(_rHand, _rightHandTrack, RightFingerBones);
            }

            // 4. keep the lowest foot on the floor, then add root motion on top
            if (keepFeetOnGround)
            {
                float footY = Mathf.Min(CharPos(B(HumanBodyBones.LeftFoot)).y, CharPos(B(HumanBodyBones.RightFoot)).y);
                _groundOffset = Mathf.Lerp(_groundOffset, _restFootHeight - footY, 0.5f);
                B(HumanBodyBones.Hips).position += _root.TransformVector(Vector3.up * _groundOffset);
            }
            if (rootMotion)
            {
                _rootSmoothed = Vector3.Lerp(_rootSmoothed, _rootTarget, 1f - rootSmoothing);
                float metresToCharacter = _charLegLength / Mathf.Max(0.3f, _personLegLength);
                B(HumanBodyBones.Hips).position += _root.TransformVector(_rootSmoothed * metresToCharacter);
            }
        }

        // ------------------------------------------------------------------ root motion

        NormalizedLandmarkList ReadNormalizedPose()
        {
            var holistic = tracker as CoreHolisticTrackingSolution;
            if (holistic != null) return holistic.poseLandmarks;
            var pose = tracker as CorePoseTrackingSolution;
            return pose != null ? pose.poseLandmarks : null;
        }

        /// <summary>Makes the current spot in the room the zero point for root motion.</summary>
        public void Recenter()
        {
            _rootCalibrated = false;
            _rootTarget = _rootSmoothed = Vector3.zero;
            _footBaseline = float.NaN;
        }

        // Pinhole-camera estimate of where the hips are in the room (metres, camera space).
        void EstimateRoot(LandmarkList world, NormalizedLandmarkList image)
        {
            if (image == null || image.Landmark.Count < COUNT || world.Landmark.Count < COUNT) return;
            var w = world.Landmark;
            var n = image.Landmark;
            if (Mathf.Min(n[L_HIP].Visibility, n[R_HIP].Visibility, n[L_SH].Visibility, n[R_SH].Visibility) < visibilityThreshold)
                return; // can't see the torso well enough: keep the last estimate

            float W = 640f, H = 480f;
            var source = ImageSourceProvider.ImageSource;
            if (source != null && source.textureWidth > 0 && source.textureHeight > 0)
            {
                W = source.textureWidth;
                H = source.textureHeight;
            }
            float focal = 0.5f * H / Mathf.Tan(0.5f * cameraVerticalFov * Mathf.Deg2Rad);

            // Distance: the torso's size in pixels vs its real size in metres (both measured flat on the
            // image plane, so leaning or turning shrinks both equally).
            int[] segments = { L_SH, R_SH, L_HIP, R_HIP, L_SH, L_HIP, R_SH, R_HIP };
            float pixels = 0f, metres = 0f;
            for (int i = 0; i < segments.Length; i += 2)
            {
                var a = n[segments[i]]; var b = n[segments[i + 1]];
                pixels += new Vector2((a.X - b.X) * W, (a.Y - b.Y) * H).magnitude;
                var wa = w[segments[i]]; var wb = w[segments[i + 1]];
                metres += new Vector2(wa.X - wb.X, wa.Y - wb.Y).magnitude;
            }
            if (pixels < 1f || metres < 0.05f) return;
            float z = focal * metres / pixels;

            float u = (n[L_HIP].X + n[R_HIP].X) * 0.5f * W - 0.5f * W;
            var cam = new Vector3(u * z / focal, 0f, z);

            // Jumps: height of the lowest ankle above where it was when standing.
            float footUp = float.NaN;
            if (Mathf.Min(n[L_ANK].Visibility, n[R_ANK].Visibility) >= visibilityThreshold)
            {
                float lowestAnkle = Mathf.Max(n[L_ANK].Y, n[R_ANK].Y) * H - 0.5f * H;
                footUp = -lowestAnkle * z / focal;
            }

            // Real leg length, to convert metres into the character's size.
            if (Mathf.Min(n[L_KNEE].Visibility, n[L_ANK].Visibility) >= visibilityThreshold)
            {
                float leg = Distance(w[L_HIP], w[L_KNEE]) + Distance(w[L_KNEE], w[L_ANK]);
                if (leg > 0.4f && leg < 1.3f) _personLegLength = Mathf.Lerp(_personLegLength, leg, 0.05f);
            }

            if (!_rootCalibrated)
            {
                _rootOrigin = cam;
                _rootCalibrated = true;
            }
            if (float.IsNaN(_footBaseline)) _footBaseline = footUp;

            Vector3 d = cam - _rootOrigin;
            var target = Vector3.zero;
            if (rootHorizontal)
            {
                target.x = mirror ? d.x : -d.x; // you face the camera, so camera-left is your right
                target.z = -d.z;                 // walking towards the camera = walking forwards
            }
            if (rootVertical && !float.IsNaN(footUp) && !float.IsNaN(_footBaseline))
                target.y = Mathf.Max(0f, footUp - _footBaseline);
            _rootTarget = target;
        }

        static float Distance(Landmark a, Landmark b) =>
            Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));

        void SolveHandFromHandLandmarks(EndBone hand, HandTrack track, HumanBodyBones[][] fingers)
        {
            if (!track.Fresh) return; // hand not visible: fingers stay relaxed (rest pose)
            var p = track.p;

            // The hand tracker sees the palm much better than the body tracker, so re-aim the hand bone too.
            if (driveHands)
                SolveEnd(hand, (p[5] + p[17]) * 0.5f - p[0], Vector3.Cross(p[5] - p[0], p[17] - p[0]));

            for (int f = 0; f < fingers.Length; f++)
            {
                var chain = fingers[f];
                var lm = FingerLandmarks[f];
                for (int k = 0; k < chain.Length; k++)
                {
                    int id = (int)chain[k];
                    var bone = _bones[id];
                    if (bone == null || !_hasAlong[id]) break;
                    Vector3 target = _root.TransformDirection(p[lm[k + 1]] - p[lm[k]]);
                    Vector3 current = bone.rotation * _alongLocal[id];
                    if (target.sqrMagnitude < 1e-8f || current.sqrMagnitude < 1e-12f) continue;
                    // swing the joint so it points where the tracked finger segment points
                    bone.rotation = Quaternion.FromToRotation(current, target) * bone.rotation;
                }
            }
        }

        void SolveLimb(Limb l, Vector3 hint)
        {
            Vector3 upper = _lm[l.b] - _lm[l.a], lower = _lm[l.c] - _lm[l.b];
            if (upper.sqrMagnitude < 1e-8f || lower.sqrMagnitude < 1e-8f) return;

            // Bend plane gives the twist when the joint is bent; near-straight limbs use the body as a hint.
            float bend = Vector3.Angle(upper, lower);
            Vector3 bendSec = Vector3.Cross(upper.normalized, lower.normalized).normalized;
            Vector3 hintSec = Vector3.Cross(upper.normalized, hint).normalized;
            Vector3 sec = Vector3.Lerp(hintSec, bendSec, Mathf.Clamp01((bend - 10f) / 25f));
            if (sec.sqrMagnitude < 0.05f) sec = l.prevSec; else l.prevSec = sec;

            SetFromRest(l.upper, upper, sec, l.restUpperDir, l.restSec);
            SetFromRest(l.lower, lower, sec, l.restLowerDir, l.restSec);
        }

        void SolveEnd(EndBone e, Vector3 primary, Vector3 sec)
        {
            if (!e.valid || primary.sqrMagnitude < 1e-8f || sec.sqrMagnitude < 1e-10f) return;
            SetFromRest(e.bone, primary, sec, e.restPrimary, e.restSec);
        }

        void SetFromRest(HumanBodyBones bone, Vector3 dir, Vector3 sec, Vector3 restDir, Vector3 restSec)
        {
            if (B(bone) == null) return;
            Quaternion now = Basis2(dir, sec), rest = Basis2(restDir, restSec);
            B(bone).rotation = _root.rotation * (now * Quaternion.Inverse(rest)) * _restCharRot[(int)bone];
        }

        void SetCharRot(HumanBodyBones bone, Quaternion delta)
        {
            var t = B(bone);
            if (t != null) t.rotation = _root.rotation * delta * _restCharRot[(int)bone];
        }

        // Frame from a "right" vector and an "up" vector (forward = right x up).
        static Quaternion Basis(Vector3 right, Vector3 up, Quaternion fallback)
        {
            Vector3 fwd = Vector3.Cross(right, up);
            if (fwd.sqrMagnitude < 1e-10f || up.sqrMagnitude < 1e-10f) return fallback;
            return Quaternion.LookRotation(fwd, up);
        }

        // Frame from a primary direction and a secondary (perpendicular-ish) direction.
        static Quaternion Basis2(Vector3 primary, Vector3 secondary)
        {
            if (Vector3.Cross(primary, secondary).sqrMagnitude < 1e-12f) return Quaternion.LookRotation(primary);
            return Quaternion.LookRotation(primary, secondary);
        }

        // ------------------------------------------------------------------ status

        void UpdateStatus()
        {
            if (tracker == null)
                _status = "No tracker found: open the FullBodyAvatarMocap scene (needs CoreHolisticTrackingSolution).";
            else if (_lastDataTime < 0f)
                _status = "Waiting for tracker... (webcam starting / model loading)";
            else if (Time.unscaledTime - _lastDataTime > 1f)
                _status = "No body detected: step back so your whole body is in view.";
            else if (!_lowerBodyVisible)
                _status = "Too close: step back so your hips (and ideally feet) are in view.";
            else
            {
                _status = "Tracking ✓";
                if (driveFingers)
                {
                    if (_fingerBoneCount == 0) _status += "   (this rig has no finger bones)";
                    else if (!(tracker is CoreHolisticTrackingSolution)) _status += "   (fingers need the Holistic tracker)";
                    else _status += $"   hands: L {(_leftHandTrack.Fresh ? "✓" : "–")}  R {(_rightHandTrack.Fresh ? "✓" : "–")}";
                }
            }
        }

        public string Status => _status;
        public bool IsTracking => _status.StartsWith("Tracking ✓");

        // ------------------------------------------------------------------ debug view

        static readonly int[] PoseLines =
        {
            11, 12, 11, 13, 13, 15, 12, 14, 14, 16, 11, 23, 12, 24, 23, 24, 23, 25, 25, 27, 24, 26, 26, 28,
            27, 29, 29, 31, 27, 31, 28, 30, 30, 32, 28, 32, 15, 17, 15, 19, 17, 19, 16, 18, 16, 20, 18, 20,
            0, 2, 2, 7, 0, 5, 5, 8, 9, 10
        };
        static readonly int[] HandLines =
        {
            0, 1, 1, 2, 2, 3, 3, 4, 0, 5, 5, 6, 6, 7, 7, 8, 9, 10, 10, 11, 11, 12,
            13, 14, 14, 15, 15, 16, 0, 17, 17, 18, 18, 19, 19, 20, 5, 9, 9, 13, 13, 17
        };

        void DrawDebugView()
        {
            var holistic = tracker as CoreHolisticTrackingSolution;
            var pose = tracker as CorePoseTrackingSolution;
            var source = ImageSourceProvider.ImageSource;
            Texture image = null;
            try { image = source != null ? source.GetCurrentTexture() : null; } catch { }

            float h = Screen.height * debugViewSize;
            float aspect = image != null && image.height > 0 ? (float)image.width / image.height : 4f / 3f;
            float w = h * aspect;
            var panel = new Rect(Screen.width - w - 10, 10, w, h);
            GUI.Box(new Rect(panel.x - 2, panel.y - 2, panel.width + 4, panel.height + 4), GUIContent.none);
            if (image != null) GUI.DrawTexture(panel, image, UnityEngine.ScaleMode.StretchToFill);

            NormalizedLandmarkList body = holistic != null ? holistic.poseLandmarks : pose != null ? pose.poseLandmarks : null;
            if (body != null && body.Landmark.Count >= COUNT)
            {
                for (int i = 0; i < PoseLines.Length; i += 2)
                {
                    var a = body.Landmark[PoseLines[i]];
                    var b = body.Landmark[PoseLines[i + 1]];
                    bool good = a.Visibility >= visibilityThreshold && b.Visibility >= visibilityThreshold;
                    DrawLine(ToPanel(panel, a.X, a.Y), ToPanel(panel, b.X, b.Y), good ? Color.green : Color.red, 2f);
                }
            }
            if (holistic != null)
            {
                DrawHand(panel, holistic.leftHandLandmarks, Color.cyan);
                DrawHand(panel, holistic.rightHandLandmarks, Color.magenta);
            }
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(11, Screen.height / 70) };
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(panel.x + 4, panel.yMax - 22, panel.width, 22),
                "green = tracked, red = guessed, cyan = left hand, magenta = right hand", style);
        }

        void DrawHand(Rect panel, NormalizedLandmarkList hand, Color color)
        {
            if (hand == null || hand.Landmark.Count < 21) return;
            for (int i = 0; i < HandLines.Length; i += 2)
            {
                var a = hand.Landmark[HandLines[i]];
                var b = hand.Landmark[HandLines[i + 1]];
                DrawLine(ToPanel(panel, a.X, a.Y), ToPanel(panel, b.X, b.Y), color, 1.5f);
            }
        }

        Vector2 ToPanel(Rect panel, float x, float y)
        {
            if (debugFlipX) x = 1f - x;
            if (debugFlipY) y = 1f - y;
            return new Vector2(panel.x + x * panel.width, panel.y + y * panel.height);
        }

        static void DrawLine(Vector2 a, Vector2 b, Color color, float width)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            if (length < 0.5f) return;
            var saved = GUI.matrix;
            var savedColor = GUI.color;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), Texture2D.whiteTexture);
            GUI.matrix = saved;
            GUI.color = savedColor;
        }

        void OnGUI()
        {
            if (showDebugView) DrawDebugView();
            if (!showStatus) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = Mathf.Max(14, Screen.height / 45), alignment = TextAnchor.MiddleLeft };
            style.normal.textColor = IsTracking ? Color.green : Color.yellow;
            GUI.Box(new Rect(10, Screen.height - 50, Mathf.Min(Screen.width - 20, 700), 40), "  Mocap: " + _status, style);
        }
    }
}
