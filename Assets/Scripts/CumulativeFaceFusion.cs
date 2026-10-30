using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>Accumulating local geometry prototype; demo identities are not reconstructed people.</summary>
public class CumulativeFaceFusion : MonoBehaviour
{
    [SerializeField] private Transform head;
    [SerializeField] private AuthorityVisualController visualController;
    [SerializeField] private bool showControls = true;
    [SerializeField] private bool automaticRegion = true;
    [SerializeField] private FaceRegion region = FaceRegion.Nose;
    [SerializeField, Range(0, 1)] private float highInfluence = .85f;
    [SerializeField, Range(0, 1)] private float lowInfluence = .2f;
    [SerializeField, Min(.05f)] private float transitionSeconds = 1.5f;
    [SerializeField, Range(.01f, .3f)] private float maximumDisplacement = .15f;
    private MeshFilter source;
    private Mesh originalMesh, runtimeMesh;
    private Vector3[] neutral, displayed, accumulated, target;
    private Vector3[] localNeutral;
    private Bounds bounds;
    private string fingerprint, status = "Initializing...";
    private int demoIdentity, nextRegion, evaluations;
    private float manualInfluence = .85f;
    private bool externalTarget;
    private Coroutine transition;
    private static readonly string[] Regions = { "Eyes", "Nose", "Mouth", "Contour", "Whole face" };
    private static readonly FaceRegion[] Cycle = { FaceRegion.Eyes, FaceRegion.Nose, FaceRegion.Mouth, FaceRegion.Contour };
    public string SavePath => Path.Combine(Application.persistentDataPath, "Test2-cumulative-face.json");
    public int EvaluationCount => evaluations;
    public bool IsReady => runtimeMesh != null;
    public string TemplateFingerprint => fingerprint;
    [Serializable] private class State
    {
        public int version = 1, evaluations, nextRegion;
        public string fingerprint;
        public Vector3[] vertices;
    }

    private void Start()
    {
        if (head == null) { enabled = false; return; }
        foreach (var filter in head.GetComponentsInChildren<MeshFilter>())
            if (filter.sharedMesh != null && filter.GetComponent<Renderer>() != null &&
                (source == null || filter.GetComponent<Renderer>().bounds.size.y > source.GetComponent<Renderer>().bounds.size.y)) source = filter;
        if (source == null || !source.sharedMesh.isReadable)
        { status = "Head mesh missing or not readable."; Debug.LogError(status, this); return; }
        originalMesh = source.sharedMesh;
        neutral = originalMesh.vertices;
        fingerprint = Fingerprint(originalMesh);
        runtimeMesh = Instantiate(originalMesh); runtimeMesh.name = "Test2 cumulative face (runtime)";
        runtimeMesh.MarkDynamic(); source.sharedMesh = runtimeMesh;
        displayed = (Vector3[])neutral.Clone(); accumulated = (Vector3[])neutral.Clone();
        localNeutral = new Vector3[neutral.Length];
        for (int i = 0; i < neutral.Length; i++)
        {
            localNeutral[i] = head.InverseTransformPoint(source.transform.TransformPoint(neutral[i]));
            if (i == 0) bounds = new Bounds(localNeutral[i], Vector3.zero); else bounds.Encapsulate(localNeutral[i]);
        }
        SelectDemo(0);
        if (visualController != null)
        { visualController.HighStarted += ApplyHigh; visualController.LowStarted += ApplyLow; }
    }

    public static string Fingerprint(Mesh mesh)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(mesh.vertexCount);
            foreach (var v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
            writer.Write(mesh.subMeshCount);
            for (int s = 0; s < mesh.subMeshCount; s++)
            { var indices = mesh.GetIndices(s); writer.Write((int)mesh.GetTopology(s)); writer.Write(indices.Length); foreach (int index in indices) writer.Write(index); }
            writer.Flush(); using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
        }
    }

    public void SelectDemo(int identity)
    {
        if (!IsReady) return;
        demoIdentity = identity == 0 ? 0 : 1; externalTarget = false;
        target = new Vector3[neutral.Length];
        for (int i = 0; i < neutral.Length; i++)
            target[i] = source.transform.InverseTransformPoint(head.TransformPoint(CumulativeFaceMath.DemoTarget(localNeutral[i], bounds, demoIdentity)));
        status = "Simulated viewer " + (demoIdentity == 0 ? "A" : "B") + " selected (not camera capture).";
    }

    /// <summary>Receiver for a future reconstructor: registered vertices in SOURCE MESH local coordinates.</summary>
    public bool SetRegisteredTarget(Vector3[] vertices, string neutralFingerprint)
    {
        if (!IsReady || neutralFingerprint != fingerprint || !ValidVertices(vertices))
        { status = "Rejected target: wrong template, ordering, coordinates or displacement."; return false; }
        target = (Vector3[])vertices.Clone(); externalTarget = true; status = "Registered external target ready."; return true;
    }

    private bool ValidVertices(Vector3[] vertices)
    {
        if (vertices == null || vertices.Length != neutral.Length) return false;
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = vertices[i];
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return false;
            var delta = head.InverseTransformVector(source.transform.TransformVector(p - neutral[i]));
            if (delta.magnitude > bounds.size.magnitude * maximumDisplacement + 1e-6f) return false;
        }
        return true;
    }

    private void ApplyHigh() => ApplyEvaluation(highInfluence);
    private void ApplyLow() => ApplyEvaluation(lowInfluence);

    /// <summary>Call once per completed utterance with normalized influence, not once per audio frame.</summary>
    public void ApplyEvaluation(float influence)
    {
        if (!isActiveAndEnabled || !IsReady || target == null || float.IsNaN(influence) || float.IsInfinity(influence)) return;
        var selected = automaticRegion ? Cycle[nextRegion % Cycle.Length] : region;
        var result = new Vector3[neutral.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = CumulativeFaceMath.Blend(accumulated[i], target[i], influence, CumulativeFaceMath.Mask(localNeutral[i], bounds, selected));
        if (!ValidVertices(result)) { status = "Rejected unsafe geometry."; return; }
        if (transition != null) StopCoroutine(transition);
        // Commit exactly once. Subsequent evaluations use this committed result, even during animation.
        accumulated = result; evaluations++; if (automaticRegion) nextRegion = (nextRegion + 1) % Cycle.Length;
        status = "#" + evaluations + ": " + selected + " / " + Mathf.RoundToInt(Mathf.Clamp01(influence) * 100) + "%";
        transition = StartCoroutine(AnimateTo(result));
    }

    private IEnumerator AnimateTo(Vector3[] destination)
    {
        var start = (Vector3[])displayed.Clone(); float elapsed = 0;
        while (elapsed < transitionSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / Mathf.Max(.05f, transitionSeconds)));
            for (int i = 0; i < displayed.Length; i++) displayed[i] = Vector3.Lerp(start[i], destination[i], t);
            UpdateMesh(); yield return null;
        }
        Array.Copy(destination, displayed, displayed.Length); UpdateMesh(); transition = null;
    }

    private void UpdateMesh()
    { runtimeMesh.vertices = displayed; runtimeMesh.RecalculateNormals(); runtimeMesh.RecalculateBounds(); }

    public void ResetFace()
    {
        if (!IsReady) return;
        if (transition != null) StopCoroutine(transition); transition = null;
        accumulated = (Vector3[])neutral.Clone(); displayed = (Vector3[])neutral.Clone();
        evaluations = nextRegion = 0; UpdateMesh(); status = "Neutral restored; saved file unchanged until Save.";
    }

    public void SaveFace()
    {
        if (!IsReady) return;
        try
        {
            string temp = SavePath + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(new State { fingerprint = fingerprint, vertices = accumulated, evaluations = evaluations, nextRegion = nextRegion }));
            if (File.Exists(SavePath)) File.Replace(temp, SavePath, null); else File.Move(temp, SavePath);
            status = "Saved current geometry."; Debug.Log("Cumulative face saved: " + SavePath, this);
        }
        catch (Exception e) { status = "Save failed: " + e.Message; }
    }

    public void LoadFace()
    {
        if (!IsReady) return;
        try
        {
            var state = JsonUtility.FromJson<State>(File.ReadAllText(SavePath));
            if (state == null || state.version != 1 || state.fingerprint != fingerprint || !ValidVertices(state.vertices) || state.evaluations < 0 || state.nextRegion < 0 || state.nextRegion >= Cycle.Length)
                throw new InvalidDataException("Save does not match this head template or limits.");
            if (transition != null) StopCoroutine(transition); transition = null;
            accumulated = state.vertices; displayed = (Vector3[])accumulated.Clone();
            evaluations = state.evaluations; nextRegion = state.nextRegion; UpdateMesh(); status = "Saved face loaded.";
        }
        catch (Exception e) { status = "Load failed: " + e.Message; }
    }

    private void OnGUI()
    {
        if (!showControls) return;
        GUILayout.BeginArea(new Rect(12, 12, 370, 365), GUI.skin.box);
        GUILayout.Label("TEST 2 | Cumulative geometry prototype");
        GUILayout.Label(externalTarget ? "Registered external target" : "SIMULATED VIEWERS - no face reconstruction");
        GUI.enabled = IsReady;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Viewer A")) SelectDemo(0);
        if (GUILayout.Button("Viewer B")) SelectDemo(1);
        GUILayout.EndHorizontal();
        automaticRegion = GUILayout.Toggle(automaticRegion, "Auto cycle: Eyes > Nose > Mouth > Contour");
        if (!automaticRegion) region = (FaceRegion)GUILayout.SelectionGrid((int)region, Regions, 3);
        GUILayout.Label("Influence: " + Mathf.RoundToInt(manualInfluence * 100) + "%");
        manualInfluence = GUILayout.HorizontalSlider(manualInfluence, 0, 1);
        if (GUILayout.Button("Apply once")) ApplyEvaluation(manualInfluence);
        GUILayout.Label("H: strong voice band | L: weak voice band");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) SaveFace();
        if (GUILayout.Button("Load")) LoadFace();
        if (GUILayout.Button("Reset face")) ResetFace();
        GUILayout.EndHorizontal();
        GUI.enabled = true; GUILayout.Label(status); GUILayout.EndArea();
    }

    private void OnDisable()
    {
        if (!IsReady || accumulated == null) return;
        if (transition != null) StopCoroutine(transition);
        transition = null; Array.Copy(accumulated, displayed, displayed.Length); UpdateMesh();
    }

    private void OnDestroy()
    {
        if (visualController != null)
        { visualController.HighStarted -= ApplyHigh; visualController.LowStarted -= ApplyLow; }
        if (source != null && originalMesh != null) source.sharedMesh = originalMesh;
        if (runtimeMesh != null) Destroy(runtimeMesh);
    }
}
