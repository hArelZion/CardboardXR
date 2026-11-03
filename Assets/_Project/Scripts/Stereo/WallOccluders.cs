using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Real walls hide what's behind them in passthrough (iPhone 11 port). Without LiDAR there's no
    // depth image to test virtual things against, so a browser screen or a cube past a wall was drawn
    // over it, as if seen through the wall. Each detected vertical plane gets an invisible copy of its
    // mesh that writes depth only (CardboardXR/DepthMask, as the portal uses), set a little behind the
    // surface so things on or at it still show; virtual things behind it then fail the depth test and
    // the camera image shows instead. Only in passthrough, where the room is seen, and only for walls
    // big enough to trust. Put it on the XR Origin with the ARPlaneManager. Logs [Room].
    public class WallOccluders : MonoBehaviour
    {
        // Smaller planes are often a cupboard door or a wrong guess.
        const float k_MinArea = 0.4f;
        // How far behind the detected surface the occluder sits, in meters.
        const float k_Setback = 0.05f;

        [Tooltip("A CardboardXR/DepthMask material; referenced here so the shader is in the build.")]
        [SerializeField] Material m_DepthMask;

        ARPlaneManager m_Planes;
        PassthroughController m_Passthrough;
        Material m_Material;
        readonly Dictionary<TrackableId, (MeshFilter filter, MeshRenderer renderer)> m_Occluders = new();
        int m_Shown;

        void Awake()
        {
            m_Planes = GetComponent<ARPlaneManager>();
            m_Passthrough = FindAnyObjectByType<PassthroughController>();
            var shader = m_DepthMask != null ? m_DepthMask.shader : Shader.Find("CardboardXR/DepthMask");
            if (shader != null && shader.isSupported)
                m_Material = new Material(shader) { name = "Wall Occluder" };
            else
                Debug.LogWarning("[Room] The depth mask shader isn't in the build: walls won't hide what's behind them");
        }

        void OnDestroy()
        {
            if (m_Material != null)
                Destroy(m_Material);
        }

        void LateUpdate()
        {
            if (m_Material == null || m_Planes == null)
                return;
            var on = m_Passthrough != null && m_Passthrough.IsOn && m_Planes.enabled;
            var shown = 0;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane.alignment != PlaneAlignment.Vertical || !plane.TryGetComponent<MeshFilter>(out var source))
                    continue;
                if (!m_Occluders.TryGetValue(plane.trackableId, out var occluder) || occluder.renderer == null)
                {
                    occluder = Create(plane);
                    m_Occluders[plane.trackableId] = occluder;
                }
                var show = on && plane.trackingState == TrackingState.Tracking && source.sharedMesh != null &&
                           plane.size.x * plane.size.y >= k_MinArea;
                if (show)
                {
                    occluder.filter.sharedMesh = source.sharedMesh;
                    shown++;
                }
                occluder.renderer.enabled = show;
            }
            if (shown != m_Shown)
                Debug.Log($"[Room] {shown} walls hide what's behind them");
            m_Shown = shown;
        }

        (MeshFilter, MeshRenderer) Create(ARPlane plane)
        {
            var occluder = new GameObject("Wall Occluder", typeof(MeshFilter), typeof(MeshRenderer)) { layer = plane.gameObject.layer };
            // The plane's mesh lies in its local x-z, with y along the normal, toward the viewer.
            occluder.transform.SetParent(plane.transform, false);
            occluder.transform.localPosition = Vector3.down * k_Setback;
            var renderer = occluder.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;
            return (occluder.GetComponent<MeshFilter>(), renderer);
        }
    }
}
