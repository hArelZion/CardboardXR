using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using CardboardXR.UI;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Camera passthrough in the viewer. Each new ARCore camera image is drawn with ARCore's own
    // background material into a texture, with its display transform widened so the texture holds
    // the whole camera image instead of the screen-shaped crop ARCore shows. A screen quad in front
    // of the AR camera shows it to both eyes at the angle the camera saw it, so the room appears at
    // true scale. The AR camera's pose driver moves it only with each new ARCore pose, which comes
    // with each new image, so the picture stays fixed to the room as the head turns and only its
    // edges trail. The camera sees less than a Cardboard eye, so the image is a window with a dark
    // border. Passthrough needs 6DoF, since ARCore must run, and hides the virtual environment (every
    // VirtualEnvironment, in whichever scene).
    // Switching to 3DoF turns it off until 6DoF is back. The "passthrough" launch option (1) starts
    // with it on. While it's on, the eyes move onto the camera (HeadPoseFusion.EyesAtCamera), so the
    // image is seen from where it was taken. The image is flat, so things in it only line up in both
    // eyes at its own distance; FocusDistance moves it to what the user is looking at, such as a
    // tracked hand. Anything else drawn keeps its own depth and fights that flat image, which is seen
    // as doubling, so while passthrough is on the rig's eyes move together (StereoRig.MonoEyes) and
    // the whole view becomes one flat, self-consistent window. In the single phone view the view also
    // takes the AR camera's pose itself, as an ordinary AR app does: the image then sits still on the
    // screen, at the price of the image's delay, which only matters in a viewer.
    public class PassthroughController : MonoBehaviour
    {
        static readonly int k_DisplayTransformId = Shader.PropertyToID("_UnityDisplayTransform");
        static readonly int k_HumanStencilId = Shader.PropertyToID("_HumanStencil");
        // View zoom steps the settings panel's View button cycles through, and where it starts.
        static readonly float[] k_Zooms = { 1f, 1.25f, 1.5f, 1.75f, 2f };
        const float k_DefaultZoom = 1.5f;
        const string k_ZoomKey = "CardboardXR.ViewZoom";
        // The nearest the image comes, in meters.
        const float k_MinDistance = 0.2f;
        // How quickly the image follows a change of focus: most of the way in this many seconds.
        const float k_FocusSeconds = 0.25f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [Tooltip("The stereo rig: its eyes move together while passthrough is on, so nothing drawn has " +
                 "a depth the one flat image can't match.")]
        [SerializeField] StereoRig m_Rig;
        [Tooltip("The AR camera. Its ARCameraManager supplies the images and its TrackedPoseDriver places the screen.")]
        [SerializeField] Camera m_ArCamera;
        [Tooltip("A unit quad, a child of the AR camera, that shows the camera image.")]
        [SerializeField] Renderer m_Screen;
        [Tooltip("The panel's toggle, shown selected while passthrough is on.")]
        [SerializeField] M3Button m_Toggle;
        [Tooltip("How far in front of the camera the image is shown without a focus. It's one flat image, so everything in it appears at this depth.")]
        [SerializeField] float m_Distance = 2f;
        [Tooltip("Height of the passthrough texture in pixels; its width follows the camera image's shape.")]
        [SerializeField] int m_TextureHeight = 720;
        [Tooltip("The panel's View button: cycles how much the picture is magnified, and shows it.")]
        [SerializeField] M3Button m_ZoomButton;

        ARCameraManager m_CameraManager;
        TrackedPoseDriver m_PoseDriver;
        Material m_CameraMaterial;
        Mesh m_Quad;
        CommandBuffer m_Commands;
        RenderTexture m_Texture;
        bool m_On;
        // 3DoF turned passthrough off, so it comes back with 6DoF.
        bool m_ResumeWithSixDof;
        // ArDebugView drives the AR camera itself.
        bool m_ArViewActive;
        // The image's frustum at unit distance, in the AR camera's x and y, once an image arrived.
        Vector2 m_FrustumCenter;
        Vector2 m_FrustumSize;
        bool m_HasImage;
        float? m_FocusDistance;
        float m_ShownDistance;
        float m_Zoom = k_DefaultZoom;
        // The user's hand over virtual things (iPhone 11 port): ARKit's people stencil goes into the
        // passthrough texture's alpha, and a second quad over the screen draws those texels last.
        AROcclusionManager m_Occlusion;
        Material m_PeopleMaterial;
        Renderer m_HandOverlay;
        bool m_PeopleDrawn;
        bool m_PeopleRequested = true;
        bool m_LoggedPeople;

        public bool IsOn => m_On;

        // Whether the real hand is drawn over virtual things. A demo that draws at the fingertips, as
        // painting does, turns it off, since the hand would hide the brush.
        public bool HandInFront { get; set; } = true;

        // How much the picture, and the whole view with it, is magnified while passthrough is on.
        public float Zoom => m_Zoom;

        // Where the image should be, in meters from the camera, or null for m_Distance. It eases there.
        public float? FocusDistance
        {
            get => m_FocusDistance;
            set
            {
                if (value.HasValue != m_FocusDistance.HasValue && m_On)
                    Debug.Log(value.HasValue ? $"[Passthrough] Focus at {value.Value:F2} m" : $"[Passthrough] Focus back at {m_Distance:F1} m");
                m_FocusDistance = value;
            }
        }

        void Awake()
        {
            m_CameraManager = m_ArCamera.GetComponent<ARCameraManager>();
            m_PoseDriver = m_ArCamera.GetComponent<TrackedPoseDriver>();
            m_ShownDistance = m_Distance;
            m_Occlusion = FindAnyObjectByType<AROcclusionManager>();
            m_Zoom = LaunchOptions.GetInt("zoom", 0) > 0
                ? LaunchOptions.GetInt("zoom", 0) / 100f
                : PlayerPrefs.GetFloat(k_ZoomKey, k_DefaultZoom);
            UpdateZoomButton();
        }

        // The settings panel's View button: the next magnification, kept for next time.
        public void CycleZoom()
        {
            var next = 0;
            for (var i = 0; i < k_Zooms.Length; i++)
            {
                if (k_Zooms[i] > m_Zoom + 0.01f)
                {
                    next = i;
                    break;
                }
            }
            m_Zoom = k_Zooms[next];
            PlayerPrefs.SetFloat(k_ZoomKey, m_Zoom);
            PlayerPrefs.Save();
            if (m_On && m_Rig != null)
                m_Rig.ViewZoom = m_Zoom;
            UpdateZoomButton();
            Debug.Log($"[Passthrough] View size {m_Zoom * 100f:F0}%");
        }

        void UpdateZoomButton()
        {
            if (m_ZoomButton != null)
                m_ZoomButton.Text = $"View {m_Zoom * 100f:F0}%";
        }

        // After the other components' OnEnable, so the mode set here sticks.
        void Start()
        {
            m_ArViewActive = LaunchOptions.GetInt("arView", 0) != 0;
            if (LaunchOptions.GetInt("passthrough", 0) != 0)
                SetPassthrough(true);
        }

        void OnDestroy()
        {
            if (m_On)
                m_CameraManager.frameReceived -= OnCameraFrame;
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            if (m_CameraMaterial != null)
                Destroy(m_CameraMaterial);
            if (m_PeopleMaterial != null)
                Destroy(m_PeopleMaterial);
            if (m_Quad != null)
                Destroy(m_Quad);
            m_Commands?.Release();
        }

        // ARKit's people segmentation, for the hand over virtual things, while passthrough is on. It
        // runs on the Neural Engine (A12 and later), so it costs the CPU little. Where it isn't
        // supported the hand is simply drawn as before, under everything.
        void SetPeopleSegmentation(bool on)
        {
            m_PeopleRequested = !on;
            if (m_Occlusion == null)
                return;
            // Known only once the AR session runs; LateUpdate asks again until then.
            if (on && m_Occlusion.descriptor == null)
                return;
            m_PeopleRequested = true;
            var supported = m_Occlusion.descriptor != null &&
                            m_Occlusion.descriptor.humanSegmentationStencilImageSupported == UnityEngine.XR.ARSubsystems.Supported.Supported;
            if (on && !supported)
            {
                if (!m_LoggedPeople)
                    Debug.Log("[Passthrough] People segmentation isn't supported here: the hand stays behind virtual things");
                m_LoggedPeople = true;
                return;
            }
            m_Occlusion.requestedHumanStencilMode = on
                ? UnityEngine.XR.ARSubsystems.HumanSegmentationStencilMode.Fastest
                : UnityEngine.XR.ARSubsystems.HumanSegmentationStencilMode.Disabled;
            if (on)
                m_Occlusion.enabled = true;
            Debug.Log($"[Passthrough] People segmentation {(on ? "requested" : "off")}");
        }

        // Draws ARKit's people stencil into the passthrough texture's alpha, with the camera image's own
        // transform; false if there's no stencil this frame.
        bool DrawPeople(Matrix4x4 displayTransform)
        {
            if (!HandInFront || m_Occlusion == null || !m_Occlusion.enabled ||
                m_Occlusion.currentHumanStencilMode == UnityEngine.XR.ARSubsystems.HumanSegmentationStencilMode.Disabled)
                return false;
            var stencil = m_Occlusion.humanStencilTexture;
            if (stencil == null)
                return false;
            if (m_PeopleMaterial == null)
            {
                var shader = Shader.Find("Hidden/CardboardXR/PassthroughPeople");
                if (shader == null || !shader.isSupported)
                {
                    if (!m_LoggedPeople)
                        Debug.LogWarning("[Passthrough] The people shader isn't in the build");
                    m_LoggedPeople = true;
                    return false;
                }
                m_PeopleMaterial = new Material(shader) { name = "Passthrough People" };
            }
            m_PeopleMaterial.SetTexture(k_HumanStencilId, stencil);
            m_PeopleMaterial.SetMatrix(k_DisplayTransformId, displayTransform);
            m_Commands.DrawMesh(m_Quad, Matrix4x4.identity, m_PeopleMaterial);
            if (!m_LoggedPeople)
                Debug.Log($"[Passthrough] Hand in front: people stencil {stencil.width}x{stencil.height}");
            m_LoggedPeople = true;
            return true;
        }

        // A copy of the screen quad, drawn last, showing the camera image only where a person is.
        void EnsureHandOverlay()
        {
            if (m_HandOverlay != null)
                return;
            var shader = Shader.Find("Hidden/CardboardXR/PassthroughHand");
            if (shader == null || !shader.isSupported || !m_Screen.TryGetComponent<MeshFilter>(out var filter))
                return;
            var overlay = new GameObject("Passthrough Hand", typeof(MeshFilter), typeof(MeshRenderer)) { layer = m_Screen.gameObject.layer };
            overlay.transform.SetParent(m_Screen.transform.parent, false);
            overlay.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            m_HandOverlay = overlay.GetComponent<MeshRenderer>();
            m_HandOverlay.sharedMaterial = new Material(shader) { name = "Passthrough Hand", mainTexture = m_Texture };
            m_HandOverlay.shadowCastingMode = ShadowCastingMode.Off;
            m_HandOverlay.receiveShadows = false;
            m_HandOverlay.enabled = false;
        }

        public void Toggle() => Choose(!m_On);

        // Turns it on or off as the user's own choice, so 3DoF turning it off doesn't bring it back
        // with 6DoF later.
        public void Choose(bool on)
        {
            m_ResumeWithSixDof = false;
            SetPassthrough(on);
        }

        public void SetPassthrough(bool on)
        {
            if (m_ArViewActive || on == m_On)
                return;

            m_On = on;
            if (on)
            {
                m_Fusion.SetMode(HeadPoseFusion.Mode.SixDof);
                m_CameraManager.frameReceived += OnCameraFrame;
            }
            else
            {
                m_CameraManager.frameReceived -= OnCameraFrame;
            }

            m_PoseDriver.enabled = on;
            m_Fusion.EyesAtCamera = on;
            if (m_Rig != null)
            {
                m_Rig.MonoEyes = on;
                m_Rig.ViewZoom = on ? m_Zoom : 1f;
            }
            SetPeopleSegmentation(on);
            // Shown once the first image has arrived.
            m_Screen.enabled = on && m_HasImage;
            if (!on)
                m_PeopleDrawn = false;
            if (m_HandOverlay != null && !on)
                m_HandOverlay.enabled = false;
            VirtualEnvironment.SetAllHidden(on);
            if (m_Toggle != null)
                m_Toggle.Selected = on;
            Debug.Log(on ? "[Passthrough] On" : "[Passthrough] Off");
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            var source = m_CameraManager.cameraMaterial;
            if (source == null || args.textures == null || args.textures.Count == 0 ||
                !args.displayMatrix.HasValue || !args.projectionMatrix.HasValue)
                return;
            // Always the whole camera image, in both views. Mono once cropped it to ARCore's
            // screen-shaped rectangle so it filled the screen, as an ordinary AR app does, but seeing
            // everything the camera saw was better even at the price of a border around it.
            if (!TryWidenDisplayTransform(args.displayMatrix.Value, out var widened, out var min, out var max))
                return;

            if (m_CameraMaterial == null)
            {
                m_CameraMaterial = new Material(source) { name = "Passthrough Camera" };
                EnableRenderPipelineKeyword();
            }
            for (var i = 0; i < args.textures.Count; i++)
                m_CameraMaterial.SetTexture(args.propertyNameIds[i], args.textures[i]);
            m_CameraMaterial.SetMatrix(k_DisplayTransformId, widened);

            var size = max - min;
            EnsureTexture(Screen.width * size.x / (Screen.height * size.y));
            DrawCameraImage(widened);

            // Screen UV maps to NDC as 2uv - 1, which ARCore's projection maps to camera space at unit
            // distance as (ndc + p02) / p00 across and (ndc + p12) / p11 up.
            var projection = args.projectionMatrix.Value;
            m_FrustumSize = new Vector2(2f * size.x / projection.m00, 2f * size.y / projection.m11);
            m_FrustumCenter = new Vector2(
                (min.x + max.x - 1f + projection.m02) / projection.m00,
                (min.y + max.y - 1f + projection.m12) / projection.m11);
            if (m_HasImage)
                return;

            m_HasImage = true;
            m_Screen.enabled = m_On;
            Debug.Log($"[Passthrough] Camera image {m_Texture.width}x{m_Texture.height}, " +
                      $"{2f * Mathf.Atan(m_FrustumSize.x / 2f) * Mathf.Rad2Deg:F0} x " +
                      $"{2f * Mathf.Atan(m_FrustumSize.y / 2f) * Mathf.Rad2Deg:F0} deg");
            LogCameraMaterial(args);
        }

        // ARCore's background shader maps screen UV to camera-texture UV as
        // tex = u * (m00, m01) + v * (m10, m11) + (m20, m21), and only screen UV 0 to 1 is drawn: a
        // crop of the camera image shaped like the screen. This finds the screen-UV rectangle the
        // whole texture covers, from min to max, and widens the transform so that screen UV 0 to 1
        // covers all of it.
        public static bool TryWidenDisplayTransform(Matrix4x4 display, out Matrix4x4 widened, out Vector2 min, out Vector2 max)
        {
            widened = display;
            min = max = Vector2.zero;
            var c0 = new Vector2(display.m00, display.m01);
            var c1 = new Vector2(display.m10, display.m11);
            var c2 = new Vector2(display.m20, display.m21);
            var determinant = c0.x * c1.y - c1.x * c0.y;
            if (Mathf.Abs(determinant) < 1e-6f)
                return false;

            Vector2 ScreenUv(Vector2 texture)
            {
                var t = texture - c2;
                return new Vector2((c1.y * t.x - c1.x * t.y) / determinant, (c0.x * t.y - c0.y * t.x) / determinant);
            }

            var a = ScreenUv(new Vector2(0f, 0f));
            var b = ScreenUv(new Vector2(1f, 0f));
            var c = ScreenUv(new Vector2(0f, 1f));
            var d = ScreenUv(new Vector2(1f, 1f));
            min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            var size = max - min;
            widened.m00 = c0.x * size.x;
            widened.m01 = c0.y * size.x;
            widened.m10 = c1.x * size.y;
            widened.m11 = c1.y * size.y;
            var offset = min.x * c0 + min.y * c1 + c2;
            widened.m20 = offset.x;
            widened.m21 = offset.y;
            return true;
        }

        void EnsureTexture(float aspect)
        {
            var height = Mathf.Max(16, m_TextureHeight);
            var width = Mathf.Max(16, Mathf.RoundToInt(height * aspect));
            if (m_Texture != null && m_Texture.width == width && m_Texture.height == height)
                return;
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            // With a depth buffer, because both providers' background shaders declare ZWrite On and
            // write SV_Depth. Metal drops the draw when the target has no depth to write to, which
            // left the texture on the clear colour; GLES on Android tolerated it, so this only ever
            // showed up on the iPhone, as a black passthrough image with nothing logged.
            m_Texture = new RenderTexture(width, height, 24) { name = "Passthrough" };
            m_Screen.material.mainTexture = m_Texture;
            if (m_HandOverlay != null)
                m_HandOverlay.sharedMaterial.mainTexture = m_Texture;
        }

        // Reads one pixel back from the middle of the passthrough texture, once. A black quad in the
        // viewer has two quite different causes, and the frame rate and logs look identical for both:
        // either the camera image never reaches this texture, or it does and the screen that shows it
        // is what fails. This says which half of the pipeline to look at.
        Color SampleTextureCentre()
        {
            var previous = RenderTexture.active;
            var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = m_Texture;
                probe.ReadPixels(new Rect(m_Texture.width / 2f, m_Texture.height / 2f, 1f, 1f), 0, 0);
                probe.Apply();
                return probe.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(probe);
            }
        }

        // ARKit ships one background shader for both render pipelines and chooses between them with a
        // keyword. Without it the shader takes its built-in-pipeline branch, which draws black in a
        // URP project: the image arrives, the draw succeeds, and nothing reports a failure. AR
        // Foundation sets this on its own background renderer feature, which we bypass by drawing the
        // camera image ourselves. ARCore needs nothing here, because it ships a shader per pipeline,
        // so this asks the shader whether it has the keyword rather than assuming a platform.
        void EnableRenderPipelineKeyword()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            // Looked up rather than constructed: constructing one the shader lacks, as ARCore's does,
            // logs an error on Android.
            var keyword = m_CameraMaterial.shader.keywordSpace.FindKeyword("ARKIT_BACKGROUND_URP");
            if (keyword.isValid)
                m_CameraMaterial.EnableKeyword(keyword);
        }

        // What the provider actually handed us, logged once. The two platforms differ here more than
        // anywhere else: ARCore gives one RGB texture, ARKit two (luma and chroma) that its own shader
        // converts, and a variant of that shader that didn't survive into the build draws black
        // without failing. Shader, keywords and textures together say which of those is the case.
        void LogCameraMaterial(ARCameraFrameEventArgs args)
        {
            var shader = m_CameraMaterial.shader;
            var keywords = m_CameraMaterial.shaderKeywords;
            var textures = new string[args.textures.Count];
            for (var i = 0; i < args.textures.Count; i++)
            {
                var texture = args.textures[i];
                textures[i] = texture == null
                    ? "null"
                    : $"{texture.width}x{texture.height} {texture.graphicsFormat}";
            }

            Debug.Log($"[Passthrough] Shader {shader.name}, supported {shader.isSupported}, " +
                      $"passes {m_CameraMaterial.passCount}, " +
                      $"keywords {(keywords.Length == 0 ? "none" : string.Join(" ", keywords))}, " +
                      $"{args.textures.Count} textures: {string.Join(", ", textures)}");
            Debug.Log($"[Passthrough] Texture centre reads {SampleTextureCentre()}, " +
                      $"display transform {m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(0)} " +
                      $"{m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(1)}");
        }

        // Draws the camera image into the texture the way ARCameraBackground draws the screen: a unit
        // quad through an orthographic projection. Then the people stencil into its alpha, if there is one.
        void DrawCameraImage(Matrix4x4 displayTransform)
        {
            if (m_Quad == null)
                m_Quad = CreateUnitQuad();
            m_Commands ??= new CommandBuffer { name = "Passthrough Camera Image" };
            m_Commands.Clear();
            m_Commands.SetRenderTarget(m_Texture);
            m_Commands.ClearRenderTarget(false, true, Color.black);
            m_Commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -0.1f, 9.9f));
            m_Commands.DrawMesh(m_Quad, Matrix4x4.identity, m_CameraMaterial);
            m_PeopleDrawn = DrawPeople(displayTransform);
            Graphics.ExecuteCommandBuffer(m_Commands);
        }

        static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh { name = "Passthrough Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0.1f), new Vector3(0f, 1f, 0.1f), new Vector3(1f, 1f, 0.1f), new Vector3(1f, 0f, 0.1f),
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        void LateUpdate()
        {
            // In the single phone view the view takes the camera's own pose, the one the image was taken
            // from, so the image sits still on the screen instead of shaking between the fused head
            // pose and each frame's (HeadPoseFusion.FollowPose). In the viewer it stays fused.
            var follow = m_On && m_Rig != null && m_Rig.IsMono ? m_ArCamera.transform : null;
            if (m_Fusion.FollowPose != follow)
            {
                m_Fusion.FollowPose = follow;
                Debug.Log(follow != null ? "[Passthrough] Phone view: the view follows the camera's pose" :
                    "[Passthrough] The view follows the fused head pose");
            }
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (!m_On)
            {
                if (m_ResumeWithSixDof && sixDof)
                {
                    m_ResumeWithSixDof = false;
                    SetPassthrough(true);
                }
                return;
            }
            // Switching to 3DoF turns ARCore, and so the camera, off.
            if (!sixDof)
            {
                SetPassthrough(false);
                m_ResumeWithSixDof = true;
                return;
            }
            if (!m_HasImage)
                return;

            // Eased in proportion, so moving from 2 m to 0.3 m takes as long as 0.3 m to 0.045 m would.
            var target = Mathf.Clamp(m_FocusDistance ?? m_Distance, k_MinDistance, m_Distance);
            var step = 1f - Mathf.Exp(-Time.unscaledDeltaTime / k_FocusSeconds);
            m_ShownDistance = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_ShownDistance), Mathf.Log(target), step));
            var d = m_ShownDistance;
            var screen = m_Screen.transform;
            screen.SetLocalPositionAndRotation(new Vector3(m_FrustumCenter.x * d, m_FrustumCenter.y * d, d), Quaternion.identity);
            screen.localScale = new Vector3(m_FrustumSize.x * d, m_FrustumSize.y * d, 1f);

            if (!m_PeopleRequested)
                SetPeopleSegmentation(true);
            var handInFront = HandInFront && m_PeopleDrawn;
            if (handInFront)
                EnsureHandOverlay();
            if (m_HandOverlay != null)
            {
                m_HandOverlay.enabled = handInFront;
                m_HandOverlay.transform.SetLocalPositionAndRotation(screen.localPosition, screen.localRotation);
                m_HandOverlay.transform.localScale = screen.localScale;
            }
        }
    }
}
