using UnityEditor;
using UnityEngine;

namespace MocapRecording.EditorTools
{
    /// <summary>Tools > Mocap Recorder — a one-window workflow for students.</summary>
    public class MocapRecorderWindow : EditorWindow
    {
        HumanoidMocapRecorder _recorder;

        [MenuItem("Tools/Mocap Recorder")]
        static void Open() => GetWindow<MocapRecorderWindow>("Mocap Recorder");

        void OnInspectorUpdate() => Repaint(); // keep timer/countdown live

        void OnGUI()
        {
            _recorder = (HumanoidMocapRecorder)EditorGUILayout.ObjectField(
                "Character", _recorder, typeof(HumanoidMocapRecorder), true);
            if (_recorder == null) _recorder = FindAnyObjectByType<HumanoidMocapRecorder>();

            if (_recorder == null)
            {
                EditorGUILayout.HelpBox(
                    "1. Open the FullBodyAvatarMocap demo scene.\n" +
                    "2. Drag your Humanoid character (Mixamo, Ready Player Me…) into the scene, facing the camera.\n" +
                    "3. Select it and click the button below.", MessageType.Info);
                var go = Selection.activeGameObject;
                using (new EditorGUI.DisabledScope(go == null || go.GetComponent<Animator>() == null))
                {
                    if (GUILayout.Button("Set up selected character", GUILayout.Height(30)))
                        _recorder = SetUp(go);
                }
                return;
            }

            if (_recorder.GetComponent<HumanoidMocapDriver>() == null)
            {
                EditorGUILayout.HelpBox("This character has no Humanoid Mocap Driver, so it won't follow the webcam.", MessageType.Warning);
                if (GUILayout.Button("Add Humanoid Mocap Driver")) SetUp(_recorder.gameObject);
            }

            EditorGUILayout.Space();
            var so = new SerializedObject(_recorder);
            so.Update();
            using (new EditorGUI.DisabledScope(_recorder.State != HumanoidMocapRecorder.RecorderState.Idle))
            {
                EditorGUILayout.PropertyField(so.FindProperty("clipName"));
                EditorGUILayout.PropertyField(so.FindProperty("saveFolder"));
                EditorGUILayout.PropertyField(so.FindProperty("countdownSeconds"));
                EditorGUILayout.PropertyField(so.FindProperty("maxDurationSeconds"));
                EditorGUILayout.PropertyField(so.FindProperty("sampleRate"));
                EditorGUILayout.PropertyField(so.FindProperty("smoothingRadius"));
                EditorGUILayout.PropertyField(so.FindProperty("inPlace"));
                EditorGUILayout.PropertyField(so.FindProperty("loopClip"));
            }
            so.ApplyModifiedProperties();

            var drv = _recorder.GetComponent<HumanoidMocapDriver>();
            if (drv != null)
            {
                bool rm = EditorGUILayout.Toggle(new GUIContent("Root Motion (walk / jump)",
                    "Estimate movement around the room from the webcam. Experimental."), drv.rootMotion);
                if (rm != drv.rootMotion)
                {
                    Undo.RecordObject(drv, "Toggle root motion");
                    drv.rootMotion = rm;
                    if (rm && _recorder.inPlace)
                    {
                        Undo.RecordObject(_recorder, "Toggle root motion");
                        _recorder.inPlace = false; // otherwise the recorder throws the movement away
                    }
                }
                if (drv.rootMotion && _recorder.inPlace)
                    EditorGUILayout.HelpBox("Root Motion is on but 'In Place' removes it from the clip. Untick In Place.", MessageType.Warning);
            }
            EditorGUILayout.Space();

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Press Play, stand in front of the webcam, then hit Record.", MessageType.Info);
                if (GUILayout.Button("Enter Play Mode", GUILayout.Height(30))) EditorApplication.isPlaying = true;
                DrawLastSaved();
                return;
            }

            var big = new GUIStyle(EditorStyles.boldLabel) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            switch (_recorder.State)
            {
                case HumanoidMocapRecorder.RecorderState.Idle:
                    GUI.backgroundColor = new Color(1f, .45f, .45f);
                    if (GUILayout.Button("● Record", GUILayout.Height(40))) _recorder.BeginRecording();
                    GUI.backgroundColor = Color.white;
                    break;

                case HumanoidMocapRecorder.RecorderState.Countdown:
                    GUILayout.Label($"Get ready… {Mathf.CeilToInt(_recorder.CountdownRemaining)}", big, GUILayout.Height(40));
                    if (GUILayout.Button("Cancel")) _recorder.Discard();
                    break;

                case HumanoidMocapRecorder.RecorderState.Recording:
                    GUILayout.Label($"● REC  {_recorder.RecordedSeconds:0.0}s  ({_recorder.FrameCount} frames)", big, GUILayout.Height(40));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("■ Stop & Save", GUILayout.Height(30))) _recorder.StopAndSave();
                        if (GUILayout.Button("Discard", GUILayout.Height(30))) _recorder.Discard();
                    }
                    break;
            }
            DrawLastSaved();
        }

        static HumanoidMocapRecorder SetUp(GameObject go)
        {
            var animator = go.GetComponent<Animator>();
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                EditorUtility.DisplayDialog("Mocap Recorder",
                    "This character isn't a Humanoid.\n\nSelect its model file (FBX) in the Project window > " +
                    "Inspector > Rig > Animation Type: Humanoid > Apply, then try again.", "OK");
                return null;
            }
            Undo.RecordObject(animator, "Set up mocap character");
            animator.runtimeAnimatorController = null; // nothing should fight the mocap while recording
            animator.applyRootMotion = false;
            foreach (var old in go.GetComponents<TrackerPro.Unity.TrackingEventHandler>())
            {
                Undo.RecordObject(old, "Set up mocap character");
                old.enabled = false; // replaced by HumanoidMocapDriver
            }
            if (go.GetComponent<HumanoidMocapDriver>() == null) Undo.AddComponent<HumanoidMocapDriver>(go);
            var recorder = go.GetComponent<HumanoidMocapRecorder>();
            if (recorder == null) recorder = Undo.AddComponent<HumanoidMocapRecorder>(go);
            return recorder;
        }

        void DrawLastSaved()
        {
            if (_recorder == null || string.IsNullOrEmpty(_recorder.LastSavedPath)) return;
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Last clip", _recorder.LastSavedPath);
                if (GUILayout.Button("Show", GUILayout.Width(50)))
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(_recorder.LastSavedPath);
                    EditorGUIUtility.PingObject(clip);
                    Selection.activeObject = clip;
                }
            }
        }
    }
}
