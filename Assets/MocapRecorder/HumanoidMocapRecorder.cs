using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MocapRecording
{
    /// <summary>
    /// Records whatever pose the humanoid character is in (e.g. driven live by FullBodyAvatarMocap)
    /// and saves it as a retargetable *Humanoid* AnimationClip (.anim) made of muscle curves.
    /// Put it on the same GameObject as the Animator / FullBodyAvatarMocap.
    /// </summary>
    [DefaultExecutionOrder(10000)] // run AFTER FullBodyAvatarMocap.LateUpdate has posed the bones
    [RequireComponent(typeof(Animator))]
    public class HumanoidMocapRecorder : MonoBehaviour
    {
        [Header("Output")]
        public string clipName = "NewMocapClip";
        public string saveFolder = "Assets/MocapClips";

        [Header("Recording")]
        [Range(10, 120)] public int sampleRate = 30;
        [Tooltip("Seconds to wait after pressing Record so the student can step back into frame.")]
        public float countdownSeconds = 5f;
        [Tooltip("Automatically stop and save after this many seconds. 0 = no limit.")]
        public float maxDurationSeconds = 0f;
        [Tooltip("Show a big countdown / REC indicator in the Game view.")]
        public bool showOverlay = true;

        [Header("Clean-up")]
        [Tooltip("Moving-average smoothing radius in frames. Reduces webcam jitter. 0 = off.")]
        [Range(0, 10)] public int smoothingRadius = 2;
        [Tooltip("Remove horizontal root movement (good for idle/attack/emote loops).")]
        public bool inPlace = true;
        public bool loopClip = false;

        public enum RecorderState { Idle, Countdown, Recording }
        public RecorderState State { get; private set; }
        public float CountdownRemaining { get; private set; }
        public int FrameCount => _frames.Count;
        public float RecordedSeconds => _frames.Count == 0 ? 0f : _frames[_frames.Count - 1].time;
        public string LastSavedPath { get; private set; }

        struct Frame
        {
            public float time;
            public Vector3 bodyPosition;
            public Quaternion bodyRotation;
            public float[] muscles;
        }

        readonly List<Frame> _frames = new List<Frame>();
        Animator _animator;
        HumanPoseHandler _poseHandler;
        HumanPose _pose;
        float _startTime;
        float _nextSampleTime;

        void Awake() => _animator = GetComponent<Animator>();

        void OnDestroy() => _poseHandler?.Dispose();

        bool EnsurePoseHandler()
        {
            if (_poseHandler != null) return true;
            if (_animator == null) _animator = GetComponent<Animator>();
            if (_animator.avatar == null || !_animator.avatar.isHuman)
            {
                Debug.LogError($"[MocapRecorder] '{name}' needs an Animator with a Humanoid Avatar " +
                               "(select the model's FBX > Rig > Animation Type: Humanoid).", this);
                return false;
            }
            _poseHandler = new HumanPoseHandler(_animator.avatar, _animator.transform);
            return true;
        }

        // ---------------------------------------------------------------- control

        public void BeginRecording()
        {
            if (State != RecorderState.Idle || !EnsurePoseHandler()) return;
            _frames.Clear();
            CountdownRemaining = countdownSeconds;
            if (countdownSeconds > 0f) State = RecorderState.Countdown;
            else StartCapture();
        }

        public void StopAndSave()
        {
            if (State == RecorderState.Recording && _frames.Count > 1) Save();
            State = RecorderState.Idle;
        }

        public void Discard()
        {
            _frames.Clear();
            State = RecorderState.Idle;
        }

        void StartCapture()
        {
            var driver = GetComponent<HumanoidMocapDriver>();
            if (driver != null) driver.Recenter(); // where you stand when recording starts = clip origin
            _startTime = Time.time;
            _nextSampleTime = 0f;
            State = RecorderState.Recording;
        }

        // ---------------------------------------------------------------- capture

        void LateUpdate()
        {
            if (State == RecorderState.Countdown)
            {
                CountdownRemaining -= Time.deltaTime;
                if (CountdownRemaining <= 0f) StartCapture();
                return;
            }
            if (State != RecorderState.Recording) return;

            float t = Time.time - _startTime;
            if (t + 0.0001f >= _nextSampleTime)
            {
                _nextSampleTime = Mathf.Max(_nextSampleTime + 1f / sampleRate, t);
                _poseHandler.GetHumanPose(ref _pose);
                _frames.Add(new Frame
                {
                    time = t,
                    bodyPosition = _pose.bodyPosition,
                    bodyRotation = _pose.bodyRotation,
                    muscles = (float[])_pose.muscles.Clone()
                });
            }

            if (maxDurationSeconds > 0f && t >= maxDurationSeconds) StopAndSave();
        }

        void OnGUI()
        {
            if (!showOverlay || State == RecorderState.Idle) return;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.08f),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter
            };
            style.normal.textColor = State == RecorderState.Countdown ? Color.yellow : Color.red;
            string text = State == RecorderState.Countdown
                ? Mathf.CeilToInt(CountdownRemaining).ToString()
                : $"● REC {RecordedSeconds:0.0}s";
            GUI.Label(new Rect(0, 20, Screen.width, Screen.height * 0.15f), text, style);
        }

        // ---------------------------------------------------------------- clip building

        // Unity's muscle names ("Left Thumb 1 Stretched") differ from the clip property
        // names for fingers ("LeftHand.Thumb.1 Stretched"). Everything else matches.
        static readonly Regex FingerMuscle =
            new Regex(@"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (.+)$");
        static string[] _muscleCurveNames;
        static string[] MuscleCurveNames
        {
            get
            {
                if (_muscleCurveNames == null)
                {
                    var names = HumanTrait.MuscleName;
                    _muscleCurveNames = new string[names.Length];
                    for (int i = 0; i < names.Length; i++)
                        _muscleCurveNames[i] = FingerMuscle.Replace(names[i], "$1Hand.$2.$3");
                }
                return _muscleCurveNames;
            }
        }

        List<Frame> Smoothed()
        {
            int n = _frames.Count, r = smoothingRadius;
            if (r <= 0 || n < 3) return new List<Frame>(_frames);

            int muscleCount = _frames[0].muscles.Length;
            var result = new List<Frame>(n);
            for (int i = 0; i < n; i++)
            {
                int a = Mathf.Max(0, i - r), b = Mathf.Min(n - 1, i + r), count = b - a + 1;
                var pos = Vector3.zero;
                var muscles = new float[muscleCount];
                var reference = _frames[i].bodyRotation;
                var q = new Vector4();
                for (int j = a; j <= b; j++)
                {
                    var f = _frames[j];
                    pos += f.bodyPosition;
                    for (int m = 0; m < muscleCount; m++) muscles[m] += f.muscles[m];
                    var fq = f.bodyRotation;
                    float sign = Quaternion.Dot(reference, fq) < 0f ? -1f : 1f;
                    q += new Vector4(fq.x, fq.y, fq.z, fq.w) * sign;
                }
                for (int m = 0; m < muscleCount; m++) muscles[m] /= count;
                q.Normalize();
                result.Add(new Frame
                {
                    time = _frames[i].time,
                    bodyPosition = pos / count,
                    bodyRotation = new Quaternion(q.x, q.y, q.z, q.w),
                    muscles = muscles
                });
            }
            return result;
        }

#if UNITY_EDITOR
        public AnimationClip BuildClip()
        {
            var frames = Smoothed();
            int n = frames.Count;
            var clip = new AnimationClip { name = clipName, frameRate = sampleRate };

            var rootT = new[] { new Keyframe[n], new Keyframe[n], new Keyframe[n] };
            var rootQ = new[] { new Keyframe[n], new Keyframe[n], new Keyframe[n], new Keyframe[n] };
            int muscleCount = frames[0].muscles.Length;
            var muscleKeys = new Keyframe[muscleCount][];
            for (int m = 0; m < muscleCount; m++) muscleKeys[m] = new Keyframe[n];

            Vector3 origin = frames[0].bodyPosition;
            Quaternion prevQ = frames[0].bodyRotation;
            for (int i = 0; i < n; i++)
            {
                var f = frames[i];
                float t = f.time;

                Vector3 p = f.bodyPosition;
                p.x = inPlace ? 0f : p.x - origin.x;
                p.z = inPlace ? 0f : p.z - origin.z;
                rootT[0][i] = new Keyframe(t, p.x);
                rootT[1][i] = new Keyframe(t, p.y);
                rootT[2][i] = new Keyframe(t, p.z);

                Quaternion q = f.bodyRotation; // keep sign continuous to avoid flips
                if (Quaternion.Dot(prevQ, q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                prevQ = q;
                rootQ[0][i] = new Keyframe(t, q.x);
                rootQ[1][i] = new Keyframe(t, q.y);
                rootQ[2][i] = new Keyframe(t, q.z);
                rootQ[3][i] = new Keyframe(t, q.w);

                for (int m = 0; m < muscleCount; m++) muscleKeys[m][i] = new Keyframe(t, f.muscles[m]);
            }

            SetCurve(clip, "RootT.x", rootT[0]); SetCurve(clip, "RootT.y", rootT[1]); SetCurve(clip, "RootT.z", rootT[2]);
            SetCurve(clip, "RootQ.x", rootQ[0]); SetCurve(clip, "RootQ.y", rootQ[1]); SetCurve(clip, "RootQ.z", rootQ[2]); SetCurve(clip, "RootQ.w", rootQ[3]);
            var curveNames = MuscleCurveNames;
            for (int m = 0; m < muscleCount; m++) SetCurve(clip, curveNames[m], muscleKeys[m]);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loopClip;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        static void SetCurve(AnimationClip clip, string property, Keyframe[] keys)
        {
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("", typeof(Animator), property),
                new AnimationCurve(keys));
        }

        void Save()
        {
            EnsureFolder(saveFolder);
            string safeName = string.IsNullOrWhiteSpace(clipName) ? "MocapClip" : clipName.Trim();
            string path = AssetDatabase.GenerateUniqueAssetPath($"{saveFolder.TrimEnd('/')}/{safeName}.anim");
            AssetDatabase.CreateAsset(BuildClip(), path);
            AssetDatabase.SaveAssets();
            LastSavedPath = path;
            Debug.Log($"[MocapRecorder] Saved {_frames.Count} frames ({RecordedSeconds:0.0}s) to {path}");
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            string parent = slash > 0 ? folder.Substring(0, slash) : "Assets";
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
        }
#else
        void Save() => Debug.LogWarning("[MocapRecorder] Saving .anim clips only works in the Unity Editor.");
#endif
    }
}
