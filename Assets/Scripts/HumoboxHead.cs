// Humobox head motion for Mixed Reality: stretch / tilt / quarter-turn spin, relative to the VIEWER.
// In Blender these were aligned to the scene camera; on a headset they must follow the user,
// so they are exported as curves (FaceTake_###_head.json) and applied here in sync with Timeline.
//
// Setup: put this on an empty "HumoboxPivot" and make the imported Humobox model its child.
// Assign the take's PlayableDirector and its _head.json. The script re-centres the model on its
// bounds so stretch/tilt/spin pivot around the middle of the box.
using System;
using UnityEngine;
using UnityEngine.Playables;

public class HumoboxHead : MonoBehaviour
{
    [Serializable]
    class Curves
    {
        public float fps;
        public int start;
        public int end;
        public float[] headYaw;
        public float[] headRoll;
        public float[] spin;
    }

    [Tooltip("Timeline playing the take (its time drives the curves).")]
    public PlayableDirector director;
    [Tooltip("FaceTake_###_head.json exported with the take.")]
    public TextAsset headCurves;
    [Tooltip("Defaults to Camera.main (the headset).")]
    public Transform viewer;

    [Header("Look (same defaults as the Blender rig)")]
    public float stretchAmount = 0.35f;   // scale 1 + 0.35 * |yaw| along the viewer's left-right
    public float squashAmount = 0.12f;    // other axes shrink a little
    public float slideAmount = 0.35f;     // slides toward the turn (model units)
    public float tiltDegrees = 25.8f;     // at headRoll = 1
    public bool invertYaw, invertRoll, invertSpin;

    Curves _c;
    Transform _align, _stretch, _spin;
    Quaternion _tilt = Quaternion.identity;
    Quaternion _baseRotation = Quaternion.identity;  // world orientation of the box at start

    void Awake()
    {
        if (headCurves) _c = JsonUtility.FromJson<Curves>(headCurves.text);
        if (!viewer && Camera.main) viewer = Camera.main.transform;

        // Pivot chain: this (world placement) -> align (faces viewer, tilts) -> stretch -> spin -> model
        var model = transform.childCount > 0 ? transform.GetChild(0) : null;
        _align = new GameObject("Align").transform;
        _align.SetParent(transform, false);
        _stretch = new GameObject("Stretch").transform;
        _stretch.SetParent(_align, false);
        _spin = new GameObject("Spin").transform;
        _spin.SetParent(_stretch, false);
        if (model)
        {
            // centre the box on the pivot
            var rends = model.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                _align.position = b.center;
            }
            model.SetParent(_spin, true);
        }
        _baseRotation = _align.rotation;  // identity in world at start: the box keeps its placed facing
    }

    float Sample(float[] a, float t)
    {
        if (a == null || a.Length == 0 || _c == null) return 0f;
        float f = Mathf.Clamp(t * _c.fps, 0, a.Length - 1);
        int i = Mathf.FloorToInt(f);
        int j = Mathf.Min(i + 1, a.Length - 1);
        return Mathf.Lerp(a[i], a[j], f - i);
    }

    void LateUpdate()
    {
        float t = director ? (float)director.time : 0f;
        float yaw = Sample(_c?.headYaw, t) * (invertYaw ? -1 : 1);
        float roll = Sample(_c?.headRoll, t) * (invertRoll ? -1 : 1);
        float spin = Sample(_c?.spin, t) * (invertSpin ? -1 : 1);

        // Align: +Z points from the viewer to the box (flattened), so +X is the viewer's right.
        // It only orients the stretch/tilt axes; the Spin node undoes the heading so the box
        // itself does NOT turn to follow the viewer.
        Quaternion heading = _align.rotation * Quaternion.Inverse(_tilt);
        if (viewer)
        {
            Vector3 d = _align.position - viewer.position;
            d.y = 0f;
            if (d.sqrMagnitude > 1e-6f) heading = Quaternion.LookRotation(d.normalized, Vector3.up);
        }
        _tilt = Quaternion.AngleAxis(-roll * tiltDegrees, Vector3.forward);
        _align.rotation = heading * _tilt;
        float a = Mathf.Abs(yaw);
        _stretch.localScale = new Vector3(1f + stretchAmount * a, 1f - squashAmount * a, 1f - squashAmount * a);
        _stretch.localPosition = new Vector3(slideAmount * yaw, 0f, 0f);
        _spin.localRotation = Quaternion.Inverse(heading) * _baseRotation * Quaternion.AngleAxis(spin * 90f, Vector3.up);
    }
}
