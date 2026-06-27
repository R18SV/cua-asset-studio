using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;

namespace AssetCuaStudio;

public partial class MainWindow : FluentWindow
{
    public class GlbEntry : INotifyPropertyChanged
    {
        public string Full { get; set; }
        public string Display { get; set; }
        public string PackageName { get; set; }   // per-model output name (prefix + model name; user-editable)
        private string _verdict = "";
        public string Verdict { get => _verdict; set { _verdict = value; Raise(nameof(Verdict)); } }
        private bool _checked;
        public bool IsChecked { get => _checked; set { _checked = value; Raise(nameof(IsChecked)); } }

        // per-model edit settings (each model carries its own height/yaw/normalize/position)
        public bool Normalize { get; set; }
        public double Height { get; set; } = 1.8;
        public double Yaw { get; set; }
        public double PosX { get; set; }
        public double PosY { get; set; }
        public double PosZ { get; set; }
        public bool ForceOpaque { get; set; }   // Advanced "try auto-fix": force all materials opaque (bake param)
        public bool HasTransparentRisk { get; set; }   // from preview: model has transparent materials (maybe mislabeled)
        public string Collider { get; set; } = "none";  // none | box | convex | mesh
        public int Tris { get; set; }            // total triangles (from preview) — drives the collider perf warning
        public string TexTarget { get; set; } = "full";   // full | 1024 | 512 | 256 (max edge when a slot is slimmed)
        public bool TexSlimNormal { get; set; } = true;    // which slots downscale (only when TexTarget != full)
        public bool TexSlimSpec { get; set; } = true;
        public bool TexSlimAlbedo { get; set; }            // off by default: alpha-bearing albedo affects cutoff/fade
        public int AnimIndex { get; set; }   // PREVIEW/default clip only — all clips are baked (--anims), so not a bake param / not dirty
        public bool PingPong { get; set; }   // seamless loop (bake forward+reverse) — bake param
        public bool VertexColor { get; set; }   // use the unlit vertex-colour shader (bake param)
        public bool HasVColor { get; set; }      // model has COLOR_0 (drives toggle visibility); not a bake param
        public bool Textureless { get; set; }    // model has no texture maps; not a bake param
        public bool VColorResolved { get; set; } // auto-enable decision already made once (don't re-toggle on re-select)
        public bool VColorLikely => HasVColor && Textureless;   // reliable "colour lives in vertex colours" signal
        private int _animCount;
        public int AnimCount   // number of clips (from preview 'ready'); >=2 => its own --anims bundle, can't join a multi-model pack
        {
            get => _animCount;
            set { _animCount = value; if (value >= 2) IsChecked = false; Raise(nameof(CanPack)); }
        }
        public bool CanPack => AnimCount < 2;   // multi-clip models are mutually exclusive with combined packing
        // baseline = settings at import (or at last successful convert); Dirty = differs from baseline
        private bool _sNorm; private double _sHeight = 1.8; private double _sYaw;
        private double _sPX, _sPY, _sPZ; private bool _sFO; private string _sCol = "none";
        private string _sTexT = "full"; private bool _sTexN = true, _sTexS = true, _sTexA, _sPP, _sVC;
        public bool Dirty => Normalize != _sNorm || Math.Abs(Height - _sHeight) > 1e-9 || Math.Abs(Yaw - _sYaw) > 1e-6
            || Math.Abs(PosX - _sPX) > 1e-9 || Math.Abs(PosY - _sPY) > 1e-9 || Math.Abs(PosZ - _sPZ) > 1e-9 || ForceOpaque != _sFO || MatEdited || Collider != _sCol
            || TexTarget != _sTexT || TexSlimNormal != _sTexN || TexSlimSpec != _sTexS || TexSlimAlbedo != _sTexA || PingPong != _sPP || VertexColor != _sVC;
        public string DirtyMark => Dirty ? "●" : "";
        public void Touch() => Raise(nameof(DirtyMark));
        public void MarkSaved() { _sNorm = Normalize; _sHeight = Height; _sYaw = Yaw; _sPX = PosX; _sPY = PosY; _sPZ = PosZ; _sFO = ForceOpaque; _sCol = Collider;
            _sTexT = TexTarget; _sTexN = TexSlimNormal; _sTexS = TexSlimSpec; _sTexA = TexSlimAlbedo; _sPP = PingPong; _sVC = VertexColor; MatEdited = false; Raise(nameof(DirtyMark)); }

        // per-material edits from the Advanced editor (material name -> {prop: value}); only edited materials present
        public Dictionary<string, Dictionary<string, object>> MatOverrides { get; } = new();
        public bool MatEdited { get; set; }

        private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    // one row in the Advanced material editor (Unity-inspector-like). Edits fire OnEdit -> live preview + bake override.
    public class MaterialVM : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public bool HasMetal { get; set; }      // false for spec-gloss materials -> hide metallic
        public bool HasEmission { get; set; }    // material is emissive at source -> show emission controls
        public string OriginalMode { get; set; } = "opaque";   // for restoring when Force-opaque toggles off
        public System.Action OnEdit;
        private bool _loading;
        public void Load(System.Action<MaterialVM> init) { _loading = true; init(this); _loading = false; }
        public void SetModeDisplay(string mode) { _mode = mode; R(nameof(Mode)); R(nameof(IsCutout)); R(nameof(TypeColor)); R(nameof(TypeLabel)); }

        private string _mode = "opaque";
        public string Mode { get => _mode; set { _mode = value; R(nameof(Mode)); R(nameof(IsCutout)); R(nameof(TypeColor)); R(nameof(TypeLabel)); Edit(); } }
        public bool IsCutout => _mode == "cutout";
        private bool EmiActive => HasEmission && _emissionEnabled && EmissionOn;
        public string TypeLabel => _mode == "fade" ? "Transparent" : _mode == "cutout" ? "Cutout" : (EmiActive ? "Emissive" : "Opaque");
        public System.Windows.Media.Brush TypeColor => new System.Windows.Media.SolidColorBrush(
            _mode == "fade" ? System.Windows.Media.Color.FromRgb(0x5A, 0xB0, 0xFF) :
            _mode == "cutout" ? System.Windows.Media.Color.FromRgb(0xC9, 0x8A, 0xFF) :
            EmiActive ? System.Windows.Media.Color.FromRgb(0xFF, 0xD9, 0x4A) : System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
        private bool _emissionEnabled = true;
        public bool EmissionEnabled { get => _emissionEnabled; set { _emissionEnabled = value; R(nameof(EmissionEnabled)); R(nameof(TypeColor)); R(nameof(TypeLabel)); Edit(); } }

        private string _color = "#ffffff";
        public string Color { get => _color; set { _color = value; R(nameof(Color)); R(nameof(ColorBrush)); Edit(); } }
        public System.Windows.Media.Brush ColorBrush => BrushOf(_color);

        private double _metallic, _smoothness = 0.5, _emissionIntensity = 1, _normalScale = 1, _cutoff = 0.5;
        public double Metallic { get => _metallic; set { _metallic = value; R(nameof(Metallic)); Edit(); } }
        public double Smoothness { get => _smoothness; set { _smoothness = value; R(nameof(Smoothness)); Edit(); } }
        public double EmissionIntensity { get => _emissionIntensity; set { _emissionIntensity = value; R(nameof(EmissionIntensity)); Edit(); } }
        public double NormalScale { get => _normalScale; set { _normalScale = value; R(nameof(NormalScale)); Edit(); } }
        public double Cutoff { get => _cutoff; set { _cutoff = value; R(nameof(Cutoff)); Edit(); } }

        private string _emission = "#000000";
        public string Emission { get => _emission; set { _emission = value; R(nameof(Emission)); R(nameof(EmissionBrush)); R(nameof(TypeColor)); R(nameof(TypeLabel)); Edit(); } }
        public bool EmissionOn => _emission != null && _emission.ToLowerInvariant() != "#000000";
        public System.Windows.Media.Brush EmissionBrush => BrushOf(_emission);

        private bool _twoSided;
        public bool TwoSided { get => _twoSided; set { _twoSided = value; R(nameof(TwoSided)); Edit(); } }

        public bool Edited { get; private set; }
        private void Edit() { if (_loading) return; Edited = true; OnEdit?.Invoke(); }

        // current values as an override bag (sent to preview + written to the engine JSON)
        public Dictionary<string, object> ToOverride()
        {
            var d = new Dictionary<string, object> { ["mode"] = Mode, ["color"] = Color, ["smoothness"] = Smoothness,
                ["emission"] = EmissionEnabled ? Emission : "#000000",
                ["emissionIntensity"] = EmissionEnabled ? EmissionIntensity : 0.0,
                ["normalScale"] = NormalScale, ["cutoff"] = Cutoff, ["twoSided"] = TwoSided };
            if (HasMetal) d["metallic"] = Metallic;
            return d;
        }

        private static System.Windows.Media.Brush BrushOf(string hex)
        {
            try { return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
            catch { return System.Windows.Media.Brushes.Gray; }
        }
        private void R(string n) => PropertyChanged?.Invoke(this, new(n));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private readonly ObservableCollection<GlbEntry> _glbs = new();
    private Config _cfg = new();
    private Settings _settings = new();
    private bool _settingsLoading;
    private string _pendingOpenPath;   // glb passed on the command line, loaded once the preview is ready
    private bool _webReady;
    private GlbEntry _pendingPreview;
    private GlbEntry _current;
    private bool _loadingEntry;   // suppress write-back while populating the panel from a selected entry
    private string _webDir;
    private string _currentModelPath;   // file streamed to the preview for the current selection
    private int _modelGen;              // cache-buster so GLTFLoader refetches on each selection
    private readonly HashSet<string> _previewFailed = new();   // models that OOM-crashed the renderer; skip their live preview thereafter

    private class Config
    {
        public string python { get; set; } = "python";
        public string glb2cuaScript { get; set; } = "";
        public string engineExe { get; set; } = "";
        public string glb2cuaMold { get; set; } = "";
        public string outDir { get; set; } = "";
        public string personRef { get; set; } = "renlexi.obj";
    }

    public MainWindow()
    {
        InitializeComponent();

        foreach (var (_, name) in L.Languages) LangBox.Items.Add(name);
        LangBox.SelectedIndex = 0;

        GlbList.ItemsSource = _glbs;
        _glbs.CollectionChanged += (s, e) => RefreshQueueCount();
        RefreshQueueCount();

        LoadConfig();
        LoadSettings();

        // command-line / file-association / Blender-bridge entry: "AssetCuaStudio.exe model.glb" auto-loads it
        _pendingOpenPath = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => a.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        // live re-preview when height changes (DP descriptor = delegate-type agnostic)
        var dp = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(NumberBox.ValueProperty, typeof(NumberBox));
        void onNum(object s, EventArgs e) { if (!_loadingEntry) { WriteCurrentParams(); SendParams(); } }
        dp.AddValueChanged(HeightBox, onNum);
        dp.AddValueChanged(PosXBox, onNum);
        dp.AddValueChanged(PosYBox, onNum);
        dp.AddValueChanged(PosZBox, onNum);

        SetMode(advanced: false);
        Loaded += async (s, e) => await InitWeb();
        SetStatus(L.I.T("ready"));
    }

    private void LoadConfig()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(p))
                _cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(p)) ?? new Config();
        }
        catch { _cfg = new Config(); }
        // resolve engine/mold paths relative to the app folder (release config uses relative paths; dev uses absolute)
        string Abs(string x) => string.IsNullOrWhiteSpace(x) || Path.IsPathRooted(x) ? x : Path.Combine(AppContext.BaseDirectory, x);
        _cfg.engineExe = Abs(_cfg.engineExe);
        _cfg.glb2cuaScript = Abs(_cfg.glb2cuaScript);
        _cfg.glb2cuaMold = Abs(_cfg.glb2cuaMold);
    }

    // ---- persistent user settings (prefix, default output, open-folder) ----
    private class Settings
    {
        public string prefix { get; set; } = "MY_";
        public string outputDir { get; set; } = "";
        public bool openFolder { get; set; } = false;
        // 3D view navigation: which OrbitControls action each mouse button performs. Default = VAM operation habit (L/M pan, R orbit).
        public string navLeft { get; set; } = "pan";
        public string navMiddle { get; set; } = "pan";
        public string navRight { get; set; } = "orbit";
    }
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetCuaStudio", "settings.json");

    private void LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { _settings = new(); }
        // apply to UI
        _settingsLoading = true;
        PrefixBox.Text = _settings.prefix;
        OutDefaultBox.Text = string.IsNullOrWhiteSpace(_settings.outputDir) ? _cfg.outDir : _settings.outputDir;
        OpenFolderCheck.IsChecked = _settings.openFolder;
        PopulateNavBoxes();
        _settingsLoading = false;
        var defOut = string.IsNullOrWhiteSpace(_settings.outputDir) ? _cfg.outDir : _settings.outputDir;
        if (!string.IsNullOrWhiteSpace(defOut)) OutBox.Text = defOut;
    }

    private void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings)); }
        catch { }
    }

    private string Prefix => string.IsNullOrEmpty(_settings.prefix) ? "" : _settings.prefix;

    // ---- WebView2 / preview ----
    private async Task InitWeb()
    {
        _webDir = Path.Combine(AppContext.BaseDirectory, "web");
        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("appassets.local", _webDir,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        // serve the (possibly huge) model by STREAMING it from disk — base64-over-postMessage blows up on
        // big glbs (185 MB city -> ~250 MB message -> WebView2 message-size crash). ACAO:* allows the
        // appassets.local page to fetch this cross-origin host.
        core.AddWebResourceRequestedFilter("https://acua-model.local/*",
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnModelResourceRequested;
        core.ProcessFailed += (s, e) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(_currentModelPath)) _previewFailed.Add(_currentModelPath);   // remember the culprit; don't preview it again
            _webReady = false;
            try { Web.CoreWebView2?.Reload(); } catch { }   // respawn the renderer so one bad model can't brick the app
            SetStatus("Preview engine recovered. Please reselect a model.");
        });
        core.WebMessageReceived += OnWebMessage;
        core.Settings.AreDevToolsEnabled = true;
        // once preview.html is live, auto-load a model passed on the command line (Blender bridge / file association)
        if (!string.IsNullOrEmpty(_pendingOpenPath))
            core.NavigationCompleted += OnFirstNavLoadPending;
        Web.Source = new Uri("https://appassets.local/preview.html");
    }

    private void OnFirstNavLoadPending(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        Web.CoreWebView2.NavigationCompleted -= OnFirstNavLoadPending;   // one-shot
        var p = _pendingOpenPath; _pendingOpenPath = null;
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) AddPaths(new[] { p });
    }

    private void OnModelResourceRequested(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = _currentModelPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var fs = File.OpenRead(path);   // streamed by WebView2; disposed when the response completes
            var env = Web.CoreWebView2.Environment;
            e.Response = env.CreateWebResourceResponse(fs, 200, "OK",
                "Content-Type: model/gltf-binary\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache");
        }
        catch { }
    }

    private void OnWebMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json; try { json = e.TryGetWebMessageAsString(); } catch { return; }
        if (string.IsNullOrEmpty(json)) return;
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; } catch { return; }
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "host-ready")
        {
            _webReady = true;
            if (_pendingPreview != null) { var p = _pendingPreview; _pendingPreview = null; LoadPreview(p); }
        }
        else if (type == "ready")
        {
            // update the current entry's verdict + advanced lists
            int mats = root.TryGetProperty("materials", out var m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;
            int anims = root.TryGetProperty("anims", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
            var warns = new List<string>();
            if (root.TryGetProperty("warns", out var w) && w.ValueKind == JsonValueKind.Array)
                foreach (var x in w.EnumerateArray()) warns.Add(x.GetString());
            if (_current != null)
            {
                _current.Verdict = warns.Count > 0 ? "⚠" : "✓";
                _current.HasTransparentRisk = warns.Any(x => x != null && x.Contains("transparent"));
                if (root.TryGetProperty("tris", out var tv) && tv.ValueKind == JsonValueKind.Number) _current.Tris = tv.GetInt32();
                _current.HasVColor = root.TryGetProperty("hasVColor", out var hv) && hv.ValueKind == JsonValueKind.True;
                _current.Textureless = root.TryGetProperty("textureless", out var tl) && tl.ValueKind == JsonValueKind.True;
                if (!_current.VColorResolved)   // auto-enable vertex-colour mode once for texture-less + vertex-coloured models
                {
                    _current.VertexColor = _current.VColorLikely; _current.VColorResolved = true;
                }
                UpdateRiskHint(); UpdateColliderWarn(); UpdateVColorHint();
            }
            BuildMaterialEditor(root);   // Advanced per-material editor (live preview + bake override)
            var animNames = root.TryGetProperty("anims", out var aa) && aa.ValueKind == JsonValueKind.Array
                ? aa.EnumerateArray().Select(x => x.GetString()).ToList() : new List<string>();
            AnimList.ItemsSource = animNames.Select(x => "• " + x).ToList();
            // Quick-mode clip picker: populate + show only when the model has multiple clips
            if (_current != null) _current.AnimCount = animNames.Count;
            PingPongRow.Visibility = animNames.Count >= 1 ? Visibility.Visible : Visibility.Collapsed;   // any animation
            VColorRow.Visibility = (_current?.HasVColor == true) ? Visibility.Visible : Visibility.Collapsed;   // only vertex-coloured models
            _loadingEntry = true;
            VColorSwitch.IsChecked = _current?.VertexColor == true;
            AnimCombo.ItemsSource = animNames;
            if (animNames.Count >= 2)
            {
                AnimRow.Visibility = Visibility.Visible;
                AnimCombo.SelectedIndex = Math.Max(0, Math.Min(_current?.AnimIndex ?? 0, animNames.Count - 1));
            }
            else AnimRow.Visibility = Visibility.Collapsed;
            _loadingEntry = false;
            if (_current?.VertexColor == true) SendParams();   // sync preview after auto-enabling vertex colours (load opts predate it)
        }
        else if (type == "gizmo" || type == "gizmoRot" || type == "gizmoScale")
        {
            ApplyGizmo(type, root);
        }
    }

    // Transform-gizmo edits from the preview (translate · rotate-yaw · uniform-scale→Height). Mirror the values into the
    // panel + selected entry, but do NOT re-post to the preview: the gizmo already moved the model live, so echoing a
    // 'params' update would fight the active drag. _loadingEntry suppresses the box/slider write-back handlers.
    private void ApplyGizmo(string type, JsonElement root)
    {
        if (_current == null) return;
        _loadingEntry = true;
        try
        {
            if (type == "gizmo" && root.TryGetProperty("pos", out var pe) && pe.ValueKind == JsonValueKind.Array && pe.GetArrayLength() >= 3)
            {
                PosXBox.Value = pe[0].GetDouble(); PosYBox.Value = pe[1].GetDouble(); PosZBox.Value = pe[2].GetDouble();
            }
            else if (type == "gizmoRot" && root.TryGetProperty("yaw", out var ye) && ye.ValueKind == JsonValueKind.Number)
            {
                double yaw = ye.GetDouble() % 360; if (yaw < 0) yaw += 360;
                YawSlider.Value = yaw; if (YawVal != null) YawVal.Text = ((int)Math.Round(yaw)) + "°";
            }
            else if (type == "gizmoScale" && root.TryGetProperty("height", out var he) && he.ValueKind == JsonValueKind.Number)
            {
                NormalizeCheck.IsChecked = true; HeightBox.IsEnabled = true; HeightBox.Value = he.GetDouble();   // uniform scale ⇒ normalize on
            }
        }
        finally { _loadingEntry = false; }
        WriteCurrentParams();   // persist into the entry + refresh the dirty mark (preview already reflects the drag)
    }

    private string RefObjUrl() => (RefSwitch.IsChecked == true)
        ? "https://appassets.local/" + Uri.EscapeDataString(_cfg.personRef) : "";

    // Full (re)load of the selected model: read bytes -> base64 -> GLTFLoader.parse (avoids cross-origin
    // fetch/CORS from the appassets.local page to the model's folder, which WebView2 virtual hosts block).
    private void LoadPreview(GlbEntry g)
    {
        if (g == null) return;
        if (!_webReady) { _pendingPreview = g; return; }
        var core = Web.CoreWebView2; if (core == null) { _pendingPreview = g; return; }
        // huge models OOM-crash the in-browser WebGL preview; skip the live render and tell the user (bake still allowed)
        long sz = 0; try { sz = new FileInfo(g.Full).Length; } catch { }
        // skip the live preview for models that would (or already did) OOM-crash the WebGL renderer.
        // 800 MB is a generous static gate; _previewFailed catches the machine-dependent middle zone after one crash.
        if (sz > 800L * 1024 * 1024 || _previewFailed.Contains(g.Full))
        {
            _currentModelPath = null;   // do NOT stream a multi-GB file into the renderer
            var warn = string.Format(L.I.T("too_big"), Path.GetFileName(g.Full), sz / 1024 / 1024);
            try { core.PostWebMessageAsString(JsonSerializer.Serialize(new { cmd = "toobig", msg = warn })); } catch { }
            SetStatus(warn);
            return;
        }
        // the model is STREAMED from disk via OnModelResourceRequested; the message stays tiny (no base64,
        // which crashes on big glbs). cache-buster query forces GLTFLoader to refetch on each selection.
        _currentModelPath = g.Full;
        var url = "https://acua-model.local/m.glb?g=" + (++_modelGen);
        var msg = new
        {
            cmd = "load",
            name = Path.GetFileName(g.Full),
            url,
            normalize = NormalizeCheck.IsChecked == true,
            height = HeightBox.Value ?? 1.8,
            yaw = YawSlider.Value,
            posX = PosXBox.Value ?? 0, posY = PosYBox.Value ?? 0, posZ = PosZBox.Value ?? 0,
            animIndex = g.AnimIndex,
            pingpong = g.PingPong,
            vertexColor = g.VertexColor,
            forceOpaque = ForceOpaqueSwitch.IsChecked == true,
            matOverrides = g.MatOverrides,
            refobj = RefObjUrl(),
            showRef = RefSwitch.IsChecked == true,
            spin = false,
            nav = NavConfig(),
        };
        try { core.PostWebMessageAsString(JsonSerializer.Serialize(msg)); } catch (Exception ex) { SetStatus(ex.Message); }
    }

    // Cheap param update — re-applies height/yaw/ref to the already-loaded model, no reload (no b64 resend).
    private void SendParams()
    {
        var core = Web?.CoreWebView2; if (core == null || !_webReady) return;
        var msg = new
        {
            cmd = "params",
            normalize = NormalizeCheck.IsChecked == true,
            height = HeightBox.Value ?? 1.8,
            yaw = YawSlider.Value,
            posX = PosXBox.Value ?? 0, posY = PosYBox.Value ?? 0, posZ = PosZBox.Value ?? 0,
            animIndex = _current?.AnimIndex ?? 0,
            pingpong = _current?.PingPong == true,
            vertexColor = _current?.VertexColor == true,
            forceOpaque = ForceOpaqueSwitch.IsChecked == true,
            matOverrides = _current?.MatOverrides,
            refobj = RefObjUrl(),
            showRef = RefSwitch.IsChecked == true,
            spin = false,
            nav = NavConfig(),
        };
        core.PostWebMessageAsString(JsonSerializer.Serialize(msg));
    }

    // ---- language / mode ----
    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = LangBox.SelectedIndex;
        if (i >= 0 && i < L.Languages.Length) L.I.SetLang(L.Languages[i].code);
        RefreshQueueCount();
        UpdatePackInfo();
        if (NavLeftBox != null) PopulateNavBoxes();   // relabel Orbit/Pan/Zoom items in the new language
        SetStatus(L.I.T("ready"));
    }

    // ---- settings (persistent) ----
    private void OnToggleSettings(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void OnSettingsChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingsLoading) return;
        _settings.prefix = PrefixBox.Text ?? "";
        _settings.outputDir = OutDefaultBox.Text ?? "";
        SaveSettings();
    }

    private void OnPickDefaultOut(object sender, RoutedEventArgs e)
    {
        using var d = new WinForms.FolderBrowserDialog();
        if (d.ShowDialog() == WinForms.DialogResult.OK)
        {
            OutDefaultBox.Text = d.SelectedPath; OnSettingsChanged(null, null);
            if (string.IsNullOrWhiteSpace(OutBox.Text)) OutBox.Text = d.SelectedPath;
        }
    }

    private void OnOpenFolderChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        _settings.openFolder = OpenFolderCheck.IsChecked == true; SaveSettings();
    }

    // ---- 3D view navigation (per-mouse-button action) ----
    private static readonly string[] NavActs = { "orbit", "pan", "zoom" };

    private void PopulateNavBoxes()
    {
        bool prev = _settingsLoading; _settingsLoading = true;   // suppress OnNavChanged while filling/selecting
        void Fill(ComboBox box, string cur)
        {
            box.Items.Clear();
            foreach (var a in NavActs) box.Items.Add(new ComboBoxItem { Content = L.I.T("nav_" + a), Tag = a });
            box.SelectedIndex = Math.Max(0, Array.IndexOf(NavActs, cur));
        }
        Fill(NavLeftBox, _settings.navLeft);
        Fill(NavMiddleBox, _settings.navMiddle);
        Fill(NavRightBox, _settings.navRight);
        _settingsLoading = prev;
    }

    private static string NavVal(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "orbit";
    private object NavConfig() => new { left = _settings.navLeft, middle = _settings.navMiddle, right = _settings.navRight };

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading) return;
        _settings.navLeft = NavVal(NavLeftBox);
        _settings.navMiddle = NavVal(NavMiddleBox);
        _settings.navRight = NavVal(NavRightBox);
        SaveSettings();
        PushNav();
    }

    // push the mapping to the live preview immediately, so changes apply without a reload
    private void PushNav()
    {
        var core = Web?.CoreWebView2; if (core == null || !_webReady) return;
        try { core.PostWebMessageAsString(JsonSerializer.Serialize(new { cmd = "nav", nav = NavConfig() })); } catch { }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.PackageName = NameBox.Text;
    }

    private void OnQuick(object sender, RoutedEventArgs e) => SetMode(advanced: false);
    private void OnAdvanced(object sender, RoutedEventArgs e) => SetMode(advanced: true);

    private void SetMode(bool advanced)
    {
        AdvancedCard.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        BtnQuick.Appearance = advanced ? ControlAppearance.Secondary : ControlAppearance.Primary;
        BtnAdvanced.Appearance = advanced ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // ---- queue management ----
    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "glTF binary|*.glb|All files|*.*" };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        using var d = new WinForms.FolderBrowserDialog();
        if (d.ShowDialog() == WinForms.DialogResult.OK) AddPaths(new[] { d.SelectedPath });
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _glbs.Clear(); _current = null; _projectPath = null; MatList.ItemsSource = null; AnimList.ItemsSource = null;
        ClearPreview();
    }

    // ---- project save / load / recent ----
    private string _projectPath;   // current project file (enables quick re-save)
    private static string SavesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetCuaStudio", "saves");

    private class ProjModel
    {
        public string full { get; set; }
        public string packageName { get; set; }
        public bool normalize { get; set; }
        public double height { get; set; }
        public double yaw { get; set; }
        public double posX { get; set; }
        public double posY { get; set; }
        public double posZ { get; set; }
        public bool forceOpaque { get; set; }
        public string collider { get; set; } = "none";
        public string texTarget { get; set; } = "full";
        public bool texSlimNormal { get; set; } = true;
        public bool texSlimSpec { get; set; } = true;
        public bool texSlimAlbedo { get; set; }
        public bool pingPong { get; set; }
        public bool vertexColor { get; set; }
        public bool vColorResolved { get; set; }
        public int animIndex { get; set; }
        public Dictionary<string, Dictionary<string, object>> matOverrides { get; set; }
    }
    private class ProjFile
    {
        public int version { get; set; } = 1;
        public string outDir { get; set; }
        public List<ProjModel> models { get; set; } = new();
    }

    // deserialized JSON values arrive as JsonElement; ApplyOverrideToVM/WriteOverrides need primitives
    private static object NormJson(object v)
    {
        if (v is JsonElement e)
            return e.ValueKind switch
            {
                JsonValueKind.Number => e.GetDouble(),
                JsonValueKind.String => e.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => e.ToString(),
            };
        return v;
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_glbs.Count == 0) { SetStatus(L.I.T("proj_empty")); return; }
        Directory.CreateDirectory(SavesDir);
        var path = _projectPath;
        if (string.IsNullOrEmpty(path))
        {
            var d = new SaveFileDialog
            {
                InitialDirectory = SavesDir,
                Filter = "Asset CUA project (*.acuaproj)|*.acuaproj",
                DefaultExt = ".acuaproj",
                FileName = Path.GetFileNameWithoutExtension(_glbs[0].Display),
            };
            if (d.ShowDialog() != true) return;
            path = d.FileName;
        }
        try
        {
            var pf = new ProjFile
            {
                outDir = OutBox.Text,
                models = _glbs.Select(g => new ProjModel
                {
                    full = g.Full, packageName = g.PackageName, normalize = g.Normalize, height = g.Height, yaw = g.Yaw,
                    posX = g.PosX, posY = g.PosY, posZ = g.PosZ, forceOpaque = g.ForceOpaque, collider = g.Collider,
                    texTarget = g.TexTarget, texSlimNormal = g.TexSlimNormal, texSlimSpec = g.TexSlimSpec, texSlimAlbedo = g.TexSlimAlbedo,
                    pingPong = g.PingPong, vertexColor = g.VertexColor, vColorResolved = g.VColorResolved, animIndex = g.AnimIndex,
                    matOverrides = g.MatOverrides.Count > 0 ? g.MatOverrides : null,
                }).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(pf, new JsonSerializerOptions { WriteIndented = true }));
            _projectPath = path;
            foreach (var g in _glbs) g.MarkSaved();
            SetStatus(L.I.T("proj_saved") + " — " + Path.GetFileName(path));
        }
        catch (Exception ex) { SetStatus("Save failed: " + ex.Message); }
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SavesDir);
        var menu = new System.Windows.Controls.ContextMenu();
        var files = new DirectoryInfo(SavesDir).GetFiles("*.acuaproj").OrderByDescending(f => f.LastWriteTime).Take(12).ToList();
        if (files.Count == 0)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = L.I.T("no_recent"), IsEnabled = false });
        else
            foreach (var f in files)
            {
                var path = f.FullName;
                var mi = new System.Windows.Controls.MenuItem { Header = Path.GetFileNameWithoutExtension(f.Name) };
                mi.Click += (s, a) => LoadProjectFrom(path);
                menu.Items.Add(mi);
            }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var open = new System.Windows.Controls.MenuItem { Header = L.I.T("open_from_file") };
        open.Click += (s, a) =>
        {
            var d = new OpenFileDialog { InitialDirectory = SavesDir, Filter = "Asset CUA project (*.acuaproj)|*.acuaproj" };
            if (d.ShowDialog() == true) LoadProjectFrom(d.FileName);
        };
        menu.Items.Add(open);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void LoadProjectFrom(string path)
    {
        ProjFile pf;
        try { pf = JsonSerializer.Deserialize<ProjFile>(File.ReadAllText(path)); }
        catch (Exception ex) { SetStatus("Open failed: " + ex.Message); return; }
        if (pf?.models == null) return;
        _glbs.Clear(); _current = null; ClearPreview();
        int missing = 0;
        foreach (var pm in pf.models)
        {
            if (!File.Exists(pm.full)) { missing++; continue; }
            var g = new GlbEntry
            {
                Full = pm.full, Display = Path.GetFileName(pm.full), Verdict = "",
                PackageName = pm.packageName, Normalize = pm.normalize, Height = pm.height, Yaw = pm.yaw,
                PosX = pm.posX, PosY = pm.posY, PosZ = pm.posZ, ForceOpaque = pm.forceOpaque, Collider = pm.collider ?? "none",
                TexTarget = pm.texTarget ?? "full", TexSlimNormal = pm.texSlimNormal, TexSlimSpec = pm.texSlimSpec, TexSlimAlbedo = pm.texSlimAlbedo,
                PingPong = pm.pingPong, VertexColor = pm.vertexColor, VColorResolved = pm.vColorResolved, AnimIndex = pm.animIndex,
            };
            if (pm.matOverrides != null)
                foreach (var kv in pm.matOverrides)
                    g.MatOverrides[kv.Key] = kv.Value.ToDictionary(x => x.Key, x => NormJson(x.Value));
            g.MarkSaved();
            _glbs.Add(g);
        }
        if (!string.IsNullOrWhiteSpace(pf.outDir)) OutBox.Text = pf.outDir;
        _projectPath = path;
        if (_glbs.Count > 0) GlbList.SelectedIndex = 0;
        SetStatus(L.I.T("proj_loaded") + " — " + Path.GetFileName(path) + (missing > 0 ? $" ({missing} {L.I.T("proj_missing")})" : ""));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string path)
        {
            var hit = _glbs.FirstOrDefault(x => x.Full == path);
            if (hit != null)
            {
                _glbs.Remove(hit);
                if (hit == _current) ClearPreview();   // don't leave the deleted model in the 3D view
            }
        }
    }

    private void ClearPreview()
    {
        _current = null; _currentModelPath = null;
        MatList.ItemsSource = null; AnimList.ItemsSource = null;
        var core = Web?.CoreWebView2;
        if (core != null && _webReady) { try { core.PostWebMessageAsString("{\"cmd\":\"clear\"}"); } catch { } }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*.glb", SearchOption.AllDirectories)) AddOne(f);
            else if (File.Exists(p)) AddOne(p);
        }
        if (string.IsNullOrWhiteSpace(OutBox.Text) && _glbs.Count > 0)
            OutBox.Text = Path.GetDirectoryName(_glbs[0].Full);
        if (_current == null && _glbs.Count > 0) { GlbList.SelectedIndex = 0; }   // selection sets the package name
    }

    private void AddOne(string f)
    {
        if (Path.GetExtension(f).ToLowerInvariant() != ".glb") return;
        if (_glbs.Any(x => x.Full == f)) return;
        _glbs.Add(new GlbEntry { Full = f, Display = Path.GetFileName(f), Verdict = "" });
    }

    private void RefreshQueueCount()
    {
        QueueCount.Text = string.Format(L.I.T("queue_n"), _glbs.Count);
        if (EmptyHint != null) EmptyHint.Visibility = _glbs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePackInfo();
    }

    private void OnGlbSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GlbList.SelectedItem is GlbEntry g)
        {
            _current = g;
            _loadingEntry = true;                    // load this model's own settings into the panel
            NameBox.Text = g.PackageName ?? (Prefix + Path.GetFileNameWithoutExtension(g.Full));
            NormalizeCheck.IsChecked = g.Normalize;
            HeightBox.IsEnabled = g.Normalize;
            HeightBox.Value = g.Height;
            YawSlider.Value = g.Yaw;
            YawVal.Text = ((int)Math.Round(g.Yaw)) + "°";
            PosXBox.Value = g.PosX; PosYBox.Value = g.PosY; PosZBox.Value = g.PosZ;
            ForceOpaqueSwitch.IsChecked = g.ForceOpaque;
            ColliderCombo.SelectedValue = g.Collider;
            TexTargetCombo.SelectedValue = g.TexTarget;
            TexNormalCheck.IsChecked = g.TexSlimNormal; TexSpecCheck.IsChecked = g.TexSlimSpec; TexAlbedoCheck.IsChecked = g.TexSlimAlbedo;
            PingPongSwitch.IsChecked = g.PingPong; VColorSwitch.IsChecked = g.VertexColor;
            AnimCombo.ItemsSource = null; AnimRow.Visibility = Visibility.Collapsed; PingPongRow.Visibility = Visibility.Collapsed; VColorRow.Visibility = Visibility.Collapsed; VColorHint.Visibility = Visibility.Collapsed;   // repopulated on the model's 'ready'
            MatList.ItemsSource = null;   // material editor rebuilt on the model's 'ready'
            _loadingEntry = false;
            UpdateRiskHint();
            LoadPreview(g);
        }
    }

    // write the panel's current values back to the selected model (marks it dirty if changed from baseline)
    private void WriteCurrentParams()
    {
        if (_current == null) return;
        _current.Normalize = NormalizeCheck.IsChecked == true;
        _current.Height = HeightBox.Value ?? 1.8;
        _current.Yaw = YawSlider.Value;
        _current.PosX = PosXBox.Value ?? 0; _current.PosY = PosYBox.Value ?? 0; _current.PosZ = PosZBox.Value ?? 0;
        _current.ForceOpaque = ForceOpaqueSwitch.IsChecked == true;
        _current.Touch();
    }

    private void OnForceOpaqueChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEntry) return;
        WriteCurrentParams(); UpdateRiskHint();
        bool fo = ForceOpaqueSwitch.IsChecked == true;   // reflect in the editor: non-edited materials show opaque
        if (MatList.ItemsSource is IEnumerable<MaterialVM> vms)
            foreach (var vm in vms) if (!vm.Edited) vm.SetModeDisplay(fo ? "opaque" : vm.OriginalMode);
        SendParams();
    }

    private void OnColliderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.Collider = (ColliderCombo.SelectedValue as string) ?? "none";
        _current.Touch(); UpdateColliderWarn();
    }

    private void OnTexTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.TexTarget = (TexTargetCombo.SelectedValue as string) ?? "full";
        _current.Touch();   // bake-only (slimming is a downscale at convert time)
    }

    private void OnTexSlimChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.TexSlimNormal = TexNormalCheck.IsChecked == true;
        _current.TexSlimSpec = TexSpecCheck.IsChecked == true;
        _current.TexSlimAlbedo = TexAlbedoCheck.IsChecked == true;
        _current.Touch();
    }

    private static int TexAuxMax(string t) => int.TryParse(t, out var v) ? v : 0;   // "1024"/"512"/"256" -> int; "full" -> 0
    private static string TexSlimSet(GlbEntry g)
    {
        var s = new List<string>();
        if (g.TexSlimNormal) s.Add("normal");
        if (g.TexSlimSpec) s.Add("spec");
        if (g.TexSlimAlbedo) s.Add("albedo");
        return string.Join(",", s);
    }

    // warn when a mesh collider would be physics-heavy in VAM (exact mesh is the real killer; convex is bounded)
    private void UpdateColliderWarn()
    {
        var col = _current?.Collider ?? "none"; int tris = _current?.Tris ?? 0;
        bool warn = (col == "mesh" && tris > 15000) || (col == "convex" && tris > 200000);
        ColliderWarn.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        if (warn) ColliderWarnText.Text = string.Format(L.I.T("collider_warn"), tris);
    }

    private void OnResetMaterials(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.MatOverrides.Clear(); _current.MatEdited = false; _current.Touch();
        ForceOpaqueSwitch.IsChecked = false;
        LoadPreview(_current);   // reloads -> 'ready' rebuilds the editor from the glb's original values
    }

    // show the "may be mislabeled — try Force opaque" hint when the model has transparent materials and the user
    // hasn't already forced opaque
    private void UpdateRiskHint()
    {
        bool risk = _current != null && _current.HasTransparentRisk && !(ForceOpaqueSwitch.IsChecked == true);
        RiskHint.Visibility = risk ? Visibility.Visible : Visibility.Collapsed;
    }

    // "auto-enabled vertex colours" notice — shown while the model is a vertex-colour candidate and VC is on
    private void UpdateVColorHint()
    {
        bool show = _current != null && _current.VColorLikely && _current.VertexColor;
        VColorHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnParamChanged(object sender, RoutedEventArgs e) => SendParams();   // RefSwitch = global preview toggle

    private void OnNormalizeChanged(object sender, RoutedEventArgs e)
    {
        HeightBox.IsEnabled = NormalizeCheck.IsChecked == true;
        if (!_loadingEntry) { WriteCurrentParams(); SendParams(); }
    }

    private void OnYawChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (YawVal != null) YawVal.Text = ((int)Math.Round(e.NewValue)) + "°";
        if (!_loadingEntry) { WriteCurrentParams(); SendParams(); }
    }

    private void OnAnimChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEntry || _current == null || AnimCombo.SelectedIndex < 0) return;
        _current.AnimIndex = AnimCombo.SelectedIndex;   // preview/default clip only — all clips bake via --anims (not dirty)
        SendParams();
    }

    private void OnPingPongChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.PingPong = PingPongSwitch.IsChecked == true; _current.Touch();
        SendParams();   // preview uses three.js LoopPingPong
    }

    private void OnVertexColorChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEntry || _current == null) return;
        _current.VertexColor = VColorSwitch.IsChecked == true; _current.Touch();
        UpdateVColorHint();
        SendParams();   // preview toggles vertexColors (grey <-> coloured)
    }

    private void OnPackChanged(object sender, RoutedEventArgs e) => UpdatePackInfo();

    // ---- Advanced per-material editor ----
    private void BuildMaterialEditor(JsonElement root)
    {
        var list = new List<MaterialVM>();
        if (root.TryGetProperty("matData", out var md) && md.ValueKind == JsonValueKind.Array)
            foreach (var m in md.EnumerateArray())
            {
                var vm = new MaterialVM { Name = GetS(m, "name") ?? "(unnamed)", HasMetal = GetB(m, "hasMetal") };
                vm.Load(v => {
                    var mode = GetS(m, "mode") ?? "opaque"; v.Mode = mode; v.OriginalMode = mode;
                    v.Color = GetS(m, "color") ?? "#ffffff";
                    v.Metallic = GetD(m, "metallic"); v.Smoothness = GetD(m, "smoothness", 0.5);
                    var emi = GetS(m, "emission") ?? "#000000";
                    v.HasEmission = emi.ToLowerInvariant() != "#000000"; v.EmissionEnabled = v.HasEmission;
                    v.Emission = emi; v.EmissionIntensity = GetD(m, "emissionIntensity", 1);
                    v.NormalScale = 1; v.Cutoff = GetD(m, "cutoff", 0.5); v.TwoSided = GetB(m, "twoSided");
                });
                if (_current != null && _current.MatOverrides.TryGetValue(vm.Name, out var ovr))
                    vm.Load(v => ApplyOverrideToVM(v, ovr));   // restore the user's earlier edits for this model
                vm.OnEdit = () => OnMaterialEdited(vm);
                list.Add(vm);
            }
        if (ForceOpaqueSwitch.IsChecked == true)   // reflect the global Force-opaque in the per-material editor
            foreach (var vm in list) if (!vm.Edited) vm.SetModeDisplay("opaque");
        MatList.ItemsSource = list;
    }

    private void OnMaterialEdited(MaterialVM vm)
    {
        if (_current == null) return;
        _current.MatOverrides[vm.Name] = vm.ToOverride();
        _current.MatEdited = true; _current.Touch();
        SendParams();   // live preview
    }

    private void OnPickBaseColor(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is MaterialVM vm) { var c = PickColor(vm.Color); if (c != null) vm.Color = c; } }
    private void OnPickEmissionColor(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is MaterialVM vm) { var c = PickColor(vm.Emission); if (c != null) vm.Emission = c; } }

    private string PickColor(string hex)
    {
        using var d = new WinForms.ColorDialog { FullOpen = true };
        try { var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
              d.Color = System.Drawing.Color.FromArgb(col.R, col.G, col.B); } catch { }
        return d.ShowDialog() == WinForms.DialogResult.OK ? $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}" : null;
    }

    private static string GetS(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool GetB(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static double GetD(JsonElement e, string k, double def = 0) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    private static void ApplyOverrideToVM(MaterialVM v, Dictionary<string, object> o)
    {
        if (o.TryGetValue("mode", out var mo)) v.Mode = mo?.ToString() ?? v.Mode;
        if (o.TryGetValue("color", out var c)) v.Color = c?.ToString() ?? v.Color;
        if (o.TryGetValue("metallic", out var me)) v.Metallic = Convert.ToDouble(me);
        if (o.TryGetValue("smoothness", out var sm)) v.Smoothness = Convert.ToDouble(sm);
        if (o.TryGetValue("emission", out var em)) v.Emission = em?.ToString() ?? v.Emission;
        if (o.TryGetValue("emissionIntensity", out var ei)) v.EmissionIntensity = Convert.ToDouble(ei);
        if (o.TryGetValue("normalScale", out var ns)) v.NormalScale = Convert.ToDouble(ns);
        if (o.TryGetValue("cutoff", out var cu)) v.Cutoff = Convert.ToDouble(cu);
        if (o.TryGetValue("twoSided", out var ts)) v.TwoSided = Convert.ToBoolean(ts);
    }

    private void OnPackModeChanged(object sender, RoutedEventArgs e) => UpdatePackInfo();

    private void UpdatePackInfo()
    {
        if (PackInfo == null) return;
        int n = _glbs.Count(x => x.IsChecked);
        bool multi = n >= 2;
        PackSwitch.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;   // the choice only exists for 2+
        if (!multi) { PackInfo.Text = n == 1 ? L.I.T("pack_one") : ""; return; }
        if (PackSwitch.IsChecked == true)   // pack into one CUA
        {
            long bytes = 0; foreach (var g in _glbs.Where(x => x.IsChecked)) { try { bytes += new FileInfo(g.Full).Length; } catch { } }
            PackInfo.Text = string.Format(L.I.T("pack_many"), n, Mb(bytes));
        }
        else                                // convert each into its own CUA (batch)
            PackInfo.Text = string.Format(L.I.T("pack_separate"), n);
    }

    private static string Mb(long b) => (b / 1048576.0).ToString("0.0") + " MB";

    // ---- pickers ----
    private void OnPickOut(object sender, RoutedEventArgs e)
    {
        using var d = new WinForms.FolderBrowserDialog();
        if (d.ShowDialog() == WinForms.DialogResult.OK) OutBox.Text = d.SelectedPath;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnQueueDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    // ---- convert ----
    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        Result.IsOpen = false;
        if (_glbs.Count == 0) { Warn(L.I.T("err_no_glb")); return; }
        var outDir = OutBox.Text;
        if (string.IsNullOrWhiteSpace(outDir)) { Warn(L.I.T("err_no_out")); return; }
        Directory.CreateDirectory(outDir);

        // checked items define the pack set; >=2 = pack into one CUA, 1 = single, 0 = fall back to selection
        var checkedItems = _glbs.Where(x => x.IsChecked).ToList();
        List<GlbEntry> targets = checkedItems.Count > 0 ? checkedItems
                               : (_current != null ? new List<GlbEntry> { _current } : new List<GlbEntry>());
        if (targets.Count == 0) { Warn(L.I.T("err_no_glb")); return; }
        bool pack = targets.Count >= 2 && PackSwitch.IsChecked == true;   // toggle off = batch into separate CUAs

        string baseName = string.IsNullOrWhiteSpace(NameBox.Text) ? (Prefix + "Pack") : NameBox.Text.Trim();

        ConvertBtn.IsEnabled = false;
        Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        int ok = 0; var errors = new List<string>();

        if (pack)
        {
            // one CUA, one global transform (engine --pack); use the first model's settings for all
            var p = targets[0];
            var outPath = Path.Combine(outDir, baseName + ".assetbundle");
            var ovr = WriteOverrides(targets);
            SetStatus(L.I.T("converting") + "  " + baseName);
            var (code, tail) = await Task.Run(() => RunEnginePack(targets.Select(t => t.Full).ToList(), outPath, p.Height, p.Normalize, p.Yaw, p.PosX, p.PosY, p.PosZ, p.AnimIndex, p.ForceOpaque, ovr, p.Collider, TexAuxMax(p.TexTarget), TexSlimSet(p), p.PingPong, p.VertexColor));
            if (code == 0 && File.Exists(outPath)) { ok = targets.Count; foreach (var t in targets) { t.Verdict = "✓"; t.MarkSaved(); } }
            else { foreach (var t in targets) t.Verdict = "✗"; errors.Add(tail); }
            Progress.Value = 100;
        }
        else
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var g = targets[i];
                var name = g.PackageName ?? (Prefix + Path.GetFileNameWithoutExtension(g.Full));   // each model's own package name
                var outPath = Path.Combine(outDir, name + ".assetbundle");
                SetStatus(L.I.T("converting") + "  " + g.Display);
                var ovr = WriteOverrides(new[] { g });
                // multi-clip model -> --anims packs ALL clips as switchable prefabs; else normal single convert
                var (code, tail) = g.AnimCount >= 2
                    ? await Task.Run(() => RunEngineAnims(g.Full, outPath, g.Height, g.Normalize, g.Yaw, g.PosX, g.PosY, g.PosZ, g.ForceOpaque, ovr, g.Collider, TexAuxMax(g.TexTarget), TexSlimSet(g), g.PingPong, g.VertexColor))
                    : await Task.Run(() => RunEngine(g.Full, name, outPath, g.Height, g.Normalize, g.Yaw, g.PosX, g.PosY, g.PosZ, g.AnimIndex, g.ForceOpaque, ovr, g.Collider, TexAuxMax(g.TexTarget), TexSlimSet(g), g.PingPong, g.VertexColor));
                if (code == 0 && File.Exists(outPath)) { ok++; g.Verdict = "✓"; g.MarkSaved(); }
                else { g.Verdict = "✗"; errors.Add($"{g.Display}: {tail}"); }
                Progress.Value = (i + 1) * 100.0 / targets.Count;
            }
        }

        ConvertBtn.IsEnabled = true; Progress.Visibility = Visibility.Collapsed;
        SetStatus(L.I.T("ready"));
        if (errors.Count == 0) Success(string.Format(L.I.T("done_n"), ok, targets.Count), outDir);
        else Warn(string.Format(L.I.T("done_n"), ok, targets.Count) + "\n" + string.Join("\n", errors.Take(4)));
    }

    private bool EngineReady(out bool useExe)
    {
        useExe = !string.IsNullOrWhiteSpace(_cfg.engineExe) && File.Exists(_cfg.engineExe);
        return useExe || (!string.IsNullOrWhiteSpace(_cfg.glb2cuaScript) && File.Exists(_cfg.glb2cuaScript));
    }

    private ProcessStartInfo NewEnginePsi(bool useExe, double height, bool normalize, double yaw, double px, double py, double pz, int animIndex, bool forceOpaque, string overridesPath, string collider, int texAuxMax, string texSlim, bool pingpong, bool vertexColor)
    {
        var psi = new ProcessStartInfo
        {
            FileName = useExe ? _cfg.engineExe : _cfg.python,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (!useExe) psi.ArgumentList.Add(_cfg.glb2cuaScript);
        if (!string.IsNullOrWhiteSpace(_cfg.glb2cuaMold)) psi.Environment["MOLD"] = _cfg.glb2cuaMold;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        psi.Environment["TARGET_HEIGHT"] = height.ToString(inv);
        psi.Environment["NORMALIZE"] = normalize ? "1" : "0";   // off = keep model's real size
        if (Math.Abs(yaw) > 0.001) psi.Environment["ORIENT_YAW"] = yaw.ToString(inv);
        if (Math.Abs(px) > 1e-9) psi.Environment["POS_X"] = px.ToString(inv);
        if (Math.Abs(py) > 1e-9) psi.Environment["POS_Y"] = py.ToString(inv);
        if (Math.Abs(pz) > 1e-9) psi.Environment["POS_Z"] = pz.ToString(inv);
        if (animIndex > 0) psi.Environment["ANIM_INDEX"] = animIndex.ToString(inv);
        if (forceOpaque) psi.Environment["FORCE_OPAQUE"] = "1";
        if (!string.IsNullOrEmpty(overridesPath)) psi.Environment["OVERRIDES"] = overridesPath;
        if (!string.IsNullOrEmpty(collider) && collider != "none") psi.Environment["COLLIDER"] = collider;
        if (texAuxMax > 0 && !string.IsNullOrEmpty(texSlim)) { psi.Environment["TEX_AUX_MAX"] = texAuxMax.ToString(); psi.Environment["TEX_SLIM"] = texSlim; }
        if (pingpong) psi.Environment["PINGPONG"] = "1";
        if (vertexColor) psi.Environment["VERTEX_COLOR"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    private (int, string) Run(ProcessStartInfo psi)
    {
        try
        {
            using var proc = Process.Start(psi);
            var soTask = proc.StandardOutput.ReadToEndAsync();   // read both concurrently — avoids a pipe-buffer deadlock
            var seTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            var so = soTask.Result; var se = seTask.Result;
            string Pick(string s) => s.Trim().Split('\n').LastOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
            // on failure surface the REAL engine error (stderr traceback), not a stdout NOTE
            var msg = proc.ExitCode != 0 && se.Trim().Length > 0 ? Pick(se) : Pick(so);
            return (proc.ExitCode, msg);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    // single: python glb2cua_mold.py <glb> <name> <out>   (or frozen exe <glb> <name> <out>)
    private (int, string) RunEngine(string glb, string name, string outPath, double height, bool normalize, double yaw, double px, double py, double pz, int animIndex, bool forceOpaque, string ovr, string collider, int texAuxMax, string texSlim, bool pingpong, bool vertexColor)
    {
        if (!EngineReady(out var useExe)) return (-1, L.I.T("err_engine"));
        var psi = NewEnginePsi(useExe, height, normalize, yaw, px, py, pz, animIndex, forceOpaque, ovr, collider, texAuxMax, texSlim, pingpong, vertexColor);
        psi.ArgumentList.Add(glb); psi.ArgumentList.Add(name); psi.ArgumentList.Add(outPath);
        return Run(psi);
    }

    // pack: python glb2cua_mold.py --pack <out> <glb1> <glb2> ...  (N models -> one switchable CUA)
    private (int, string) RunEnginePack(List<string> glbs, string outPath, double height, bool normalize, double yaw, double px, double py, double pz, int animIndex, bool forceOpaque, string ovr, string collider, int texAuxMax, string texSlim, bool pingpong, bool vertexColor)
    {
        if (!EngineReady(out var useExe)) return (-1, L.I.T("err_engine"));
        var psi = NewEnginePsi(useExe, height, normalize, yaw, px, py, pz, animIndex, forceOpaque, ovr, collider, texAuxMax, texSlim, pingpong, vertexColor);
        psi.ArgumentList.Add("--pack"); psi.ArgumentList.Add(outPath);
        foreach (var g in glbs) psi.ArgumentList.Add(g);
        return Run(psi);
    }

    // --anims: one glb with N clips -> one bundle with N switchable prefabs (VAM "Asset name" switches at runtime)
    private (int, string) RunEngineAnims(string glb, string outPath, double height, bool normalize, double yaw, double px, double py, double pz, bool forceOpaque, string ovr, string collider, int texAuxMax, string texSlim, bool pingpong, bool vertexColor)
    {
        if (!EngineReady(out var useExe)) return (-1, L.I.T("err_engine"));
        var psi = NewEnginePsi(useExe, height, normalize, yaw, px, py, pz, 0, forceOpaque, ovr, collider, texAuxMax, texSlim, pingpong, vertexColor);
        psi.ArgumentList.Add("--anims"); psi.ArgumentList.Add(outPath); psi.ArgumentList.Add(glb);
        return Run(psi);
    }

    // write the merged per-material overrides to a temp JSON for the engine's OVERRIDES env (null if none)
    private string WriteOverrides(IEnumerable<GlbEntry> targets)
    {
        var merged = new Dictionary<string, Dictionary<string, object>>();
        foreach (var t in targets) foreach (var kv in t.MatOverrides) merged[kv.Key] = kv.Value;
        if (merged.Count == 0) return null;
        var path = Path.Combine(Path.GetTempPath(), "acua_ovr_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { materials = merged }));
        return path;
    }

    // ---- helpers ----
    private void Warn(string msg)
    {
        Result.Severity = InfoBarSeverity.Warning; Result.Title = ""; Result.Message = msg; Result.IsOpen = true;
    }

    private void Success(string msg, string folder)
    {
        Result.Severity = InfoBarSeverity.Success; Result.Title = ""; Result.Message = msg + "\n" + folder; Result.IsOpen = true;
        if (OpenFolderCheck?.IsChecked == true)
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); } catch { }
    }

    private void SetStatus(string s) => StatusText.Text = s;
}
