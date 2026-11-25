using System.IO;
using RobotVacuum.Headless;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RobotVacuum
{
    /// <summary>How the intro video plays at startup (Settings › General).</summary>
    public enum IntroVideoMode
    {
        WithSound = 0,
        Muted = 1,
        Off = 2,
    }

    /// <summary>
    /// The VACSim intro video, played once when the program opens, over the start screen. A click or any key skips it;
    /// otherwise it fades out at the end. It never plays in a headless job, a worker or a batch-mode process, and not in
    /// the Unity editor unless Robot Vacuum › Play Intro Video in Play Mode is ticked.
    ///
    /// The file is StreamingAssets/Intro/vacsim-intro.mp4 (1280 × 720 H.264, AAC), played from disk by a VideoPlayer
    /// into a texture shown on a UI Toolkit panel above every other. If the file is missing or can't be played, the
    /// start screen simply appears.
    /// </summary>
    public sealed class IntroVideo : MonoBehaviour
    {
        public const string RelativePath = "Intro/vacsim-intro.mp4";
        public const string EditorPrefsKey = "RobotVacuum.IntroVideoInEditor";
        const string PrefsKey = "RobotVacuum.IntroVideo";
        const string ThemePath = "LevelEditor/LevelEditorTheme";
        const string StartSceneName = "StartScreen";

        const float Volume = 0.7f;
        const float EndFadeSeconds = 0.6f;
        const float SkipFadeSeconds = 0.25f;
        const float PrepareTimeoutSeconds = 4f;
        const float IgnoreInputSeconds = 0.3f; // a click left over from launching the program doesn't skip it
        const float HintDelaySeconds = 1f;
        const float Aspect = 16f / 9f;

        /// <summary>The video's own background, so the letterbox around it is seamless.</summary>
        static readonly Color Backdrop = new Color32(21, 27, 35, 255);

        public static IntroVideoMode Mode
        {
            get => (IntroVideoMode)Mathf.Clamp(PlayerPrefs.GetInt(PrefsKey, (int)IntroVideoMode.WithSound), 0, 2);
            set
            {
                PlayerPrefs.SetInt(PrefsKey, (int)value);
                PlayerPrefs.Save();
            }
        }

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, RelativePath);

        /// <summary>True while the video is on screen.</summary>
        public static bool Playing => current != null;

        static IntroVideo current;
        static bool startupHandled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            current = null;
            startupHandled = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void PlayAtStartup()
        {
            if (startupHandled) return;
            startupHandled = true;

            if (Application.isBatchMode || HeadlessBootstrap.IsHeadlessJob) return;
            if (Mode == IntroVideoMode.Off) return;
            if (SceneManager.GetActiveScene().name != StartSceneName) return;
#if UNITY_EDITOR
            if (!UnityEditor.EditorPrefs.GetBool(EditorPrefsKey, false)) return;
#endif
            Play(Mode == IntroVideoMode.Muted);
        }

        /// <summary>Plays the video now (Settings › General › Play now). Nothing happens if it is already playing or the file is missing.</summary>
        public static void Play(bool muted)
        {
            if (current != null) return;
            if (!File.Exists(FilePath))
            {
                Debug.LogWarning($"Intro video: {FilePath} not found; skipping it.");
                return;
            }

            var host = new GameObject("Intro Video");
            host.SetActive(false);
            var intro = host.AddComponent<IntroVideo>();
            intro.muted = muted;
            host.SetActive(true);
        }

        bool muted;
        PanelSettings panel;
        RenderTexture texture;
        VideoPlayer player;
        VisualElement root;
        VisualElement frame;
        Label hint;
        float startedAt;
        float fadeStartedAt;
        float fadeSeconds;
        bool closing;
        bool focused;

        void Awake()
        {
            current = this;
            startedAt = Time.unscaledTime;
            BuildOverlay();
            BuildPlayer();
        }

        void BuildOverlay()
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = "Intro Video";
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemePath);
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            panel.sortingOrder = 10000; // above the start screen and everything else

            var documentHost = new GameObject("Intro Video UI");
            documentHost.transform.SetParent(transform, false);
            documentHost.SetActive(false);
            var document = documentHost.AddComponent<UIDocument>();
            document.panelSettings = panel;
            documentHost.SetActive(true);

            root = document.rootVisualElement;
            root.style.flexGrow = 1f;
            root.style.backgroundColor = Backdrop;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;
            root.focusable = true;
            root.pickingMode = PickingMode.Position;

            frame = new VisualElement { name = "intro-frame", pickingMode = PickingMode.Ignore };
            root.Add(frame);

            hint = new Label("Click or press any key to skip") { pickingMode = PickingMode.Ignore };
            hint.style.position = Position.Absolute;
            hint.style.right = 24f;
            hint.style.bottom = 18f;
            hint.style.fontSize = 13f;
            hint.style.color = new Color(1f, 1f, 1f, 0.45f);
            hint.style.opacity = 0f;
            root.Add(hint);

            root.RegisterCallback<GeometryChangedEvent>(_ => Fit());
            root.RegisterCallback<PointerDownEvent>(_ => Skip());
            root.RegisterCallback<KeyDownEvent>(_ => Skip());
        }

        /// <summary>The largest 16:9 frame that fits the window, centred; the rest is the video's own backdrop colour.</summary>
        void Fit()
        {
            float width = root.layout.width, height = root.layout.height;
            if (float.IsNaN(width) || width <= 0f || height <= 0f) return;

            float frameWidth = Mathf.Min(width, height * Aspect);
            frame.style.width = frameWidth;
            frame.style.height = frameWidth / Aspect;
        }

        void BuildPlayer()
        {
            texture = new RenderTexture(1280, 720, 0) { name = "Intro Video" };
            texture.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(true, true, Backdrop); // the backdrop until the first frame arrives, not black
            RenderTexture.active = previous;
            frame.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(texture));

            player = gameObject.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            player.url = FilePath;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = texture;
            player.isLooping = false;
            player.skipOnDrop = true;
            player.waitForFirstFrame = true;

            if (muted)
            {
                player.audioOutputMode = VideoAudioOutputMode.None;
            }
            else
            {
                player.audioOutputMode = VideoAudioOutputMode.Direct;
                player.controlledAudioTrackCount = 1;
                player.EnableAudioTrack(0, true);
                player.SetDirectAudioVolume(0, Volume);
            }

            player.prepareCompleted += source =>
            {
                if (!closing) source.Play();
            };
            player.loopPointReached += _ => Close(EndFadeSeconds);
            player.errorReceived += (_, message) =>
            {
                Debug.LogWarning($"Intro video: {message}");
                Close(SkipFadeSeconds);
            };
            player.Prepare();
        }

        void Skip()
        {
            if (Time.unscaledTime - startedAt < IgnoreInputSeconds) return;
            Close(SkipFadeSeconds);
        }

        void Close(float seconds)
        {
            if (closing) return;
            closing = true;
            fadeStartedAt = Time.unscaledTime;
            fadeSeconds = Mathf.Max(0.01f, seconds);
        }

        void Update()
        {
            float now = Time.unscaledTime;

            if (!focused && root.panel != null)
            {
                root.Focus(); // so a key press reaches the overlay, not the start screen underneath
                focused = true;
            }

            if (!closing && !player.isPrepared && now - startedAt > PrepareTimeoutSeconds)
            {
                Debug.LogWarning("Intro video: it didn't load in time; skipping it.");
                Close(SkipFadeSeconds);
            }

            if (!closing) hint.style.opacity = Mathf.Clamp01((now - startedAt - HintDelaySeconds) / 0.5f);

            if (closing)
            {
                float t = Mathf.Clamp01((now - fadeStartedAt) / fadeSeconds);
                root.style.opacity = 1f - t;
                if (!muted && player.audioOutputMode == VideoAudioOutputMode.Direct) player.SetDirectAudioVolume(0, Volume * (1f - t));
                if (t >= 1f) Destroy(gameObject);
            }
        }

        void OnDestroy()
        {
            if (current == this) current = null;
            if (player != null) player.Stop();
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }
            if (panel != null) Destroy(panel);
        }
    }
}
