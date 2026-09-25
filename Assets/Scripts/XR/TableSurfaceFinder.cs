using System.Collections.Generic;
using Meta.XR;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Finds a table-like surface in the real room for something to stand on:
    /// flat, facing up, at table height, and bigger than a chair seat. Never the
    /// floor, a wall, the ceiling or a couch.
    ///
    /// Two sources, best first:
    ///  * Space Setup planes (ARPlaneManager), when the user has made one: any
    ///    upward plane in the height band, not classified floor/ceiling/couch/seat.
    ///  * Live environment depth (EnvironmentRaycastManager): a fan of rays over
    ///    the view below the horizon; upward hits in the height band are grouped
    ///    on a 10 cm grid and the largest patch is measured. Needs no Space Setup.
    ///
    /// A surface is only reported once two scans agree on it, so a stray patch
    /// of depth does not pull things onto it.
    /// </summary>
    public sealed class TableSurfaceFinder : MonoBehaviour
    {
        public struct Surface
        {
            public Vector3 centre;   // on the surface
            public float floorY;
            public float height;     // above that floor, metres
            public float shortSide, longSide;
            public string source;
        }

        const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [SerializeField] float minHeight = 0.45f;
        [SerializeField] float maxHeight = 1.2f;
        [Tooltip("A chair seat is ~0.45 m across; a table is bigger.")]
        [SerializeField] float minShortSide = 0.35f;
        [SerializeField] float minArea = 0.3f;
        [SerializeField] float maxDistance = 3.5f;
        [SerializeField] float scanInterval = 0.25f;
        [SerializeField] Vector2Int rays = new(24, 14);
        [Tooltip("Degrees below the horizon the ray fan covers.")]
        [SerializeField] Vector2 pitchRange = new(8f, 65f);
        [SerializeField] float yawHalfRange = 45f;

        public Surface? Current { get; private set; }
        public string Status { get; private set; } = "idle";

        EnvironmentRaycastManager raycaster;
        ARPlaneManager planes;
        bool running;
        float nextScan;
        Surface? pending;
        readonly List<Vector3> upHits = new();
        readonly List<float> allUpY = new();

        public void Begin()
        {
            if (running)
                return;
            running = true;
            Current = null;
            pending = null;
            planes = FindFirstObjectByType<ARPlaneManager>();
            HeadsetPermissions.Request(ScenePermission, granted =>
            {
                if (!running)
                    return;
                if (!granted)
                {
                    Status = "no spatial data permission";
                    return;
                }
                if (planes != null)
                    planes.enabled = true;
                if (EnvironmentRaycastManager.IsSupported)
                {
                    raycaster = GetComponent<EnvironmentRaycastManager>();
                    if (raycaster == null)
                        raycaster = gameObject.AddComponent<EnvironmentRaycastManager>();
                }
                Status = raycaster != null ? "scanning" : "no depth; planes only";
            });
        }

        public void End()
        {
            running = false;
            if (raycaster != null)
                Destroy(raycaster);
            raycaster = null;
            Status = "idle";
        }

        void Update()
        {
            if (!running || Time.time < nextScan)
                return;
            nextScan = Time.time + scanInterval;
            var eye = Camera.main != null ? Camera.main.transform : null;
            if (eye == null)
                return;

            var found = FromPlanes(eye.position) ?? FromDepth(eye);
            if (found == null)
                return;
            var f = found.Value;
            if (pending != null && Vector3.Distance(pending.Value.centre, f.centre) < 0.15f)
            {
                if (Current == null)
                    Debug.Log($"Table surface: {f.source}, {f.longSide:0.00} x {f.shortSide:0.00} m, " +
                              $"{f.height:0.00} m high, {Vector3.Distance(eye.position, f.centre):0.0} m away.", this);
                Current = f;
                Status = "found";
            }
            pending = f;
        }

        Surface? FromPlanes(Vector3 eye)
        {
            if (planes == null || !planes.enabled)
                return null;
            Surface? best = null;
            var bestD = float.MaxValue;
            var floorY = 0f;
            foreach (var p in planes.trackables)
                if ((p.classifications & PlaneClassifications.Floor) != 0)
                    floorY = p.center.y;
            foreach (var p in planes.trackables)
            {
                if (p.alignment != PlaneAlignment.HorizontalUp || p.trackingState == TrackingState.None)
                    continue;
                if ((p.classifications & (PlaneClassifications.Floor | PlaneClassifications.Ceiling |
                                          PlaneClassifications.SeatOfAnyType)) != 0)
                    continue;
                var h = p.center.y - floorY;
                var s = Mathf.Min(p.size.x, p.size.y);
                var l = Mathf.Max(p.size.x, p.size.y);
                if (h < minHeight || h > maxHeight || s < minShortSide || s * l < minArea)
                    continue;
                var d = Vector3.Distance(eye, p.center);
                if (d > maxDistance || d >= bestD)
                    continue;
                bestD = d;
                best = new Surface { centre = p.center, floorY = floorY, height = h, shortSide = s, longSide = l, source = "space setup plane" };
            }
            return best;
        }

        Surface? FromDepth(Transform eye)
        {
            if (raycaster == null)
                return null;
            upHits.Clear();
            allUpY.Clear();
            var flat = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
                flat = Vector3.ProjectOnPlane(eye.up, Vector3.up);
            flat.Normalize();
            for (var i = 0; i < rays.x; i++)
            for (var j = 0; j < rays.y; j++)
            {
                var yaw = Mathf.Lerp(-yawHalfRange, yawHalfRange, (i + 0.5f) / rays.x);
                var pitch = Mathf.Lerp(pitchRange.x, pitchRange.y, (j + 0.5f) / rays.y);
                var dir = Quaternion.AngleAxis(yaw, Vector3.up) * flat;
                dir = Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, dir)) * dir;
                if (!raycaster.Raycast(new Ray(eye.position, dir), out var hit, maxDistance))
                    continue;
                if (hit.normal.y < 0.92f)
                    continue;
                allUpY.Add(hit.point.y);
                upHits.Add(hit.point);
            }
            if (upHits.Count < 12)
            {
                Status = "scanning (few upward hits)";
                return null;
            }

            // Floor: the lowest height many upward hits share; else tracking floor 0.
            allUpY.Sort();
            var floorY = Mathf.Min(0f, allUpY[Mathf.Clamp(allUpY.Count / 20, 0, allUpY.Count - 1)]);
            if (eye.position.y - floorY > 2.2f)
                floorY = 0f;

            // Patches at table height on a 10 cm grid; the biggest connected one wins.
            var cells = new Dictionary<Vector2Int, List<Vector3>>();
            foreach (var h in upHits)
            {
                var ht = h.y - floorY;
                if (ht < minHeight || ht > maxHeight)
                    continue;
                var key = new Vector2Int(Mathf.FloorToInt(h.x / 0.1f), Mathf.FloorToInt(h.z / 0.1f));
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = new List<Vector3>();
                list.Add(h);
            }
            if (cells.Count < 4)
            {
                Status = "scanning (nothing at table height)";
                return null;
            }

            var seen = new HashSet<Vector2Int>();
            List<Vector3> bestPatch = null;
            foreach (var start in cells.Keys)
            {
                if (seen.Contains(start))
                    continue;
                var patch = new List<Vector3>();
                var stack = new Stack<Vector2Int>();
                stack.Push(start);
                seen.Add(start);
                var refY = cells[start][0].y;
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    patch.AddRange(cells[c]);
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var n = new Vector2Int(c.x + dx, c.y + dz);
                        if (seen.Contains(n) || !cells.TryGetValue(n, out var nl))
                            continue;
                        if (Mathf.Abs(nl[0].y - refY) > 0.05f)   // one flat top, not a step
                            continue;
                        seen.Add(n);
                        stack.Push(n);
                    }
                }
                if (bestPatch == null || patch.Count > bestPatch.Count)
                    bestPatch = patch;
            }

            // Its extent along its own main axes (5-95 %), so a long desk reads long.
            var centre = Vector3.zero;
            foreach (var p in bestPatch)
                centre += p;
            centre /= bestPatch.Count;
            float sxx = 0, szz = 0, sxz = 0;
            foreach (var p in bestPatch)
            {
                var dx = p.x - centre.x;
                var dz = p.z - centre.z;
                sxx += dx * dx; szz += dz * dz; sxz += dx * dz;
            }
            var angle = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
            var ax = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var az = new Vector2(-ax.y, ax.x);
            var along = new List<float>(bestPatch.Count);
            var across = new List<float>(bestPatch.Count);
            foreach (var p in bestPatch)
            {
                var d = new Vector2(p.x - centre.x, p.z - centre.z);
                along.Add(Vector2.Dot(d, ax));
                across.Add(Vector2.Dot(d, az));
            }
            var a = Extent(along);
            var b = Extent(across);
            var s = Mathf.Min(a, b);
            var l = Mathf.Max(a, b);
            if (s < minShortSide || s * l < minArea)
            {
                Status = $"scanning (surface {l:0.00} x {s:0.00} m too small)";
                return null;
            }
            return new Surface { centre = centre, floorY = floorY, height = centre.y - floorY, shortSide = s, longSide = l, source = "depth" };
        }

        static float Extent(List<float> v)
        {
            v.Sort();
            var lo = v[Mathf.Clamp(Mathf.FloorToInt(v.Count * 0.05f), 0, v.Count - 1)];
            var hi = v[Mathf.Clamp(Mathf.CeilToInt(v.Count * 0.95f) - 1, 0, v.Count - 1)];
            return hi - lo + 0.1f;   // plus one grid cell: hits are samples inside the edges
        }
    }
}
