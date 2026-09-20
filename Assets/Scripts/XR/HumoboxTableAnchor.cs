using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Finds a real table and pins a spatial anchor to it, for the Humobox to sit on.
    ///
    /// Tables come from the headset's Space Setup: Meta reports each surface the
    /// user marked there as a plane classified Table. The one nearest the viewer
    /// wins, and an anchor is made at its centre once, so the box stays put on it
    /// even as tracking refines. Until a table turns up -- no Space Setup, no
    /// table marked in it, or running in the Editor -- <see cref="TryGetTable"/>
    /// reports nothing and the caller falls back to placing the box in the air.
    ///
    /// Meta only hands out the Space Setup to apps granted the scene permission,
    /// which has to be asked for at runtime. Plane detection is held off until the
    /// user answers, since a plane subsystem started without it finds nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumoboxTableAnchor : MonoBehaviour
    {
        const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [Tooltip("Found in the scene when empty.")]
        [SerializeField] ARPlaneManager planeManager;

        [Tooltip("Defaults to Camera.main (the headset).")]
        [SerializeField] Transform viewer;

        [Tooltip("Tables smaller than this across, in metres, are ignored.")]
        [SerializeField] float minTableSize = 0.3f;

        ARPlane table;
        ARAnchor anchor;

        /// <summary>The anchor on the chosen table, once one is found.</summary>
        public Transform Anchor => anchor != null ? anchor.transform : null;

        void Start()
        {
            if (planeManager == null)
                planeManager = FindFirstObjectByType<ARPlaneManager>();
            if (viewer == null && Camera.main != null)
                viewer = Camera.main.transform;

            if (planeManager == null || HeadsetPermissions.Has(ScenePermission) || !MixedReality.PassthroughActive)
                return;

            planeManager.enabled = false;
            HeadsetPermissions.Request(ScenePermission, granted =>
            {
                if (granted)
                    planeManager.enabled = true;
                else
                    Debug.LogWarning("Scene permission denied: the Humobox cannot find a table and will float instead.", this);
            });
        }

        void Update()
        {
            if (anchor != null || planeManager == null || !planeManager.enabled)
                return;

            table = NearestTable();
            if (table == null)
                return;

            var surface = new GameObject("Humobox Table Anchor");
            surface.transform.SetPositionAndRotation(table.center, Quaternion.identity);
            anchor = surface.AddComponent<ARAnchor>();
            Debug.Log($"Humobox table found: {table.size.x:0.00} x {table.size.y:0.00} m, " +
                      $"{Vector3.Distance(table.center, ViewerPosition):0.0} m away.", this);
        }

        /// <summary>
        /// The table's anchor and the smaller side of its top, in metres, which
        /// bounds how big anything standing on it can be.
        /// </summary>
        public bool TryGetTable(out Transform tableAnchor, out float shortSide)
        {
            tableAnchor = Anchor;
            shortSide = table != null ? Mathf.Min(table.size.x, table.size.y) : 0f;
            return tableAnchor != null;
        }

        ARPlane NearestTable()
        {
            ARPlane best = null;
            var bestDistance = float.MaxValue;

            foreach (var plane in planeManager.trackables)
            {
                if ((plane.classifications & PlaneClassifications.Table) == 0)
                    continue;
                if (plane.trackingState == TrackingState.None)
                    continue;
                if (Mathf.Min(plane.size.x, plane.size.y) < minTableSize)
                    continue;

                var distance = Vector3.Distance(plane.center, ViewerPosition);
                if (distance < bestDistance)
                {
                    best = plane;
                    bestDistance = distance;
                }
            }

            return best;
        }

        Vector3 ViewerPosition => viewer != null ? viewer.position : Vector3.zero;
    }
}
