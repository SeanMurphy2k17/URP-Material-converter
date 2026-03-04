using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Converts Built-in Standard materials to URP Lit.
/// Handles metallic/specular workflows, all texture slots, render modes, and keywords.
/// Particle shaders: Standard Unlit/Surface → URP Particles/Unlit|Lit.
/// </summary>
public class URPMaterialConverter : EditorWindow
{
    private Vector2 _scroll;
    private readonly StringBuilder _log = new StringBuilder();
    private bool _dryRun;

    // Source shaders
    private const string STD              = "Standard";
    private const string STD_SPEC         = "Standard (Specular setup)";
    private const string PARTICLE_SURFACE = "Particles/Standard Surface";
    private const string PARTICLE_UNLIT   = "Particles/Standard Unlit";
    private const string PARTICLE_ADD     = "Particles/Additive";
    private const string PARTICLE_ALPHA   = "Particles/Alpha Blended";
    private const string PARTICLE_ALPHA_P = "Particles/Alpha Blended Premultiply";
    private const string MOBILE_ADD       = "Mobile/Particles/Additive";

    // Target shaders — editable in the GUI for custom URP setups
    private string _urpLit       = "Universal Render Pipeline/Lit";
    private string _urpPartLit   = "Universal Render Pipeline/Particles/Lit";
    private string _urpPartUnlit = "Universal Render Pipeline/Particles/Unlit";

    private string _pipelineStatus;

    void OnEnable()
    {
        var rp = GraphicsSettings.currentRenderPipeline;
        if (rp == null)
            _pipelineStatus = "WARNING: No render pipeline asset assigned (Project Settings → Graphics). URP may not be active.";
        else if (!rp.GetType().FullName.Contains("Universal"))
            _pipelineStatus = $"WARNING: Active pipeline is '{rp.GetType().Name}', not URP. Shader.Find will likely fail.";
        else
            _pipelineStatus = $"OK — URP active: {rp.name}";
    }

    [MenuItem("Tools/URP Material Converter")]
    static void Open()
    {
        var w = GetWindow<URPMaterialConverter>("URP Material Converter");
        w.minSize = new Vector2(440, 560);
        w.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("URP Material Converter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Converts Standard / Standard (Specular) / Particle shaders to URP equivalents.\n" +
            "All textures, floats, colors, render modes, and keywords are mapped exactly.",
            MessageType.Info);
        EditorGUILayout.Space(6);

        // Pipeline status
        bool isWarning = _pipelineStatus != null && _pipelineStatus.StartsWith("WARNING");
        if (_pipelineStatus != null)
            EditorGUILayout.HelpBox(_pipelineStatus, isWarning ? MessageType.Warning : MessageType.None);

        // Shader name overrides
        EditorGUILayout.LabelField("Target Shader Names", EditorStyles.boldLabel);
        _urpLit       = EditorGUILayout.TextField("Lit",            _urpLit);
        _urpPartLit   = EditorGUILayout.TextField("Particles/Lit",  _urpPartLit);
        _urpPartUnlit = EditorGUILayout.TextField("Particles/Unlit", _urpPartUnlit);
        EditorGUILayout.Space(4);

        _dryRun = EditorGUILayout.Toggle("Dry Run (log only — no changes written)", _dryRun);
        EditorGUILayout.Space(6);

        if (GUILayout.Button("Convert Selected Materials in Project Window", GUILayout.Height(28)))
            ConvertSelected();

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Pick Folder → Convert All Materials Inside", GUILayout.Height(28)))
            ConvertFolder();

        EditorGUILayout.Space(8);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Conversion Log", EditorStyles.boldLabel);
        if (GUILayout.Button("Clear", GUILayout.Width(60)))
            _log.Clear();
        EditorGUILayout.EndHorizontal();

        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        EditorGUILayout.TextArea(_log.ToString(), GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry points
    // ─────────────────────────────────────────────────────────────────────────

    void ConvertSelected()
    {
        var mats = new List<Material>();
        foreach (var obj in Selection.objects)
            if (obj is Material m) mats.Add(m);

        if (mats.Count == 0) { Log("No materials selected in the Project window."); Repaint(); return; }
        ProcessBatch(mats);
    }

    void ConvertFolder()
    {
        string folder = EditorUtility.OpenFolderPanel("Select folder containing materials", "Assets", "");
        if (string.IsNullOrEmpty(folder)) return;

        if (folder.StartsWith(Application.dataPath))
            folder = "Assets" + folder.Substring(Application.dataPath.Length);

        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { folder });
        var mats = new List<Material>(guids.Length);
        foreach (string g in guids)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
            if (mat != null) mats.Add(mat);
        }

        Log($"Found {mats.Count} material(s) in '{folder}'");
        ProcessBatch(mats);
    }

    void ProcessBatch(List<Material> mats)
    {
        int converted = 0, skipped = 0;

        if (!_dryRun) AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var mat in mats)
            {
                if (ConvertMaterial(mat)) converted++;
                else                      skipped++;
            }
        }
        finally
        {
            if (!_dryRun)
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        Log($"\n── Result: {converted} converted, {skipped} skipped ──\n");
        Repaint();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Router
    // ─────────────────────────────────────────────────────────────────────────

    bool ConvertMaterial(Material mat)
    {
        string sn = mat.shader.name;
        switch (sn)
        {
            case STD:              return ConvertStandard(mat, specular: false);
            case STD_SPEC:         return ConvertStandard(mat, specular: true);
            case PARTICLE_SURFACE: return ConvertParticle(mat, lit: true,  forceBlend: -1);
            case PARTICLE_UNLIT:   return ConvertParticle(mat, lit: false, forceBlend: -1);
            case PARTICLE_ADD:
            case MOBILE_ADD:       return ConvertParticle(mat, lit: false, forceBlend: 2); // Additive
            case PARTICLE_ALPHA:   return ConvertParticle(mat, lit: false, forceBlend: 0); // Alpha
            case PARTICLE_ALPHA_P: return ConvertParticle(mat, lit: false, forceBlend: 1); // Premultiply
            default:
                Log($"  SKIP  [{mat.name}]  (shader: {sn})");
                return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Standard → URP Lit
    // ─────────────────────────────────────────────────────────────────────────

    bool ConvertStandard(Material mat, bool specular)
    {
        Log($"  → [{mat.name}]  ({(specular ? "Specular" : "Metallic")})");

        // ── Snapshot all values BEFORE shader swap ──
        Color  baseColor          = GetColor(mat, "_Color", Color.white);
        Texture mainTex           = GetTex(mat, "_MainTex");
        Vector2 mainScale         = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex")  : Vector2.one;
        Vector2 mainOffset        = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;

        float  metallic           = GetFloat(mat, "_Metallic",             0f);
        Texture metallicGlossMap  = GetTex(mat, "_MetallicGlossMap");
        float  glossiness         = GetFloat(mat, "_Glossiness",           0.5f);  // → _Smoothness
        float  glossMapScale      = GetFloat(mat, "_GlossMapScale",        1f);
        float  smoothnessChannel  = GetFloat(mat, "_SmoothnessTextureChannel", 0f);

        Color  specColor          = GetColor(mat, "_SpecColor",  Color.white);
        Texture specGlossMap      = GetTex(mat, "_SpecGlossMap");

        Texture bumpMap           = GetTex(mat, "_BumpMap");
        float  bumpScale          = GetFloat(mat, "_BumpScale",            1f);
        Texture parallaxMap       = GetTex(mat, "_ParallaxMap");
        float  parallax           = GetFloat(mat, "_Parallax",             0.02f);

        Texture occlusionMap      = GetTex(mat, "_OcclusionMap");
        float  occlusionStrength  = GetFloat(mat, "_OcclusionStrength",    1f);

        Texture emissionMap       = GetTex(mat, "_EmissionMap");
        Color  emissionColor      = GetColor(mat, "_EmissionColor", Color.black);
        bool   emissionEnabled    = mat.IsKeywordEnabled("_EMISSION");

        Texture detailMask        = GetTex(mat, "_DetailMask");
        Texture detailAlbedo      = GetTex(mat, "_DetailAlbedoMap");
        Texture detailNormal      = GetTex(mat, "_DetailNormalMap");
        float  detailNormalScale  = GetFloat(mat, "_DetailNormalMapScale", 1f);

        float  cutoff             = GetFloat(mat, "_Cutoff",               0.5f);
        int    renderMode         = (int)GetFloat(mat, "_Mode",            0f);
        float  specHighlights     = GetFloat(mat, "_SpecularHighlights",   1f);
        float  glossyReflections  = GetFloat(mat, "_GlossyReflections",    1f);
        float  cull               = GetFloat(mat, "_Cull",                 2f);

        if (_dryRun)
        {
            Log($"    [DRY RUN] renderMode={renderMode}, specular={specular}");
            return true;
        }

        Shader urpLit = Shader.Find(_urpLit);
        if (urpLit == null) { Log($"    ERROR: '{_urpLit}' not found — check the shader name field at the top of the window."); return false; }

        mat.shader = urpLit;

        // ── Base color / albedo ──
        mat.SetColor("_BaseColor", baseColor);
        mat.SetTexture("_BaseMap", mainTex);
        mat.SetTextureScale("_BaseMap",  mainScale);
        mat.SetTextureOffset("_BaseMap", mainOffset);

        // ── Workflow ──
        if (specular)
        {
            mat.SetFloat("_WorkflowMode", 0f);
            mat.SetColor("_SpecColor", specColor);
            if (specGlossMap != null) mat.SetTexture("_SpecGlossMap", specGlossMap);
        }
        else
        {
            mat.SetFloat("_WorkflowMode", 1f);
            mat.SetFloat("_Metallic", metallic);
            if (metallicGlossMap != null) mat.SetTexture("_MetallicGlossMap", metallicGlossMap);
        }

        // ── Smoothness ──  Standard _Glossiness → URP _Smoothness
        mat.SetFloat("_Smoothness",              glossiness);
        mat.SetFloat("_GlossMapScale",           glossMapScale);
        mat.SetFloat("_SmoothnessTextureChannel", smoothnessChannel);

        // ── Normal / Height ──
        if (bumpMap != null) mat.SetTexture("_BumpMap", bumpMap);
        mat.SetFloat("_BumpScale", bumpScale);
        if (parallaxMap != null) mat.SetTexture("_ParallaxMap", parallaxMap);
        mat.SetFloat("_Parallax", parallax);

        // ── Occlusion ──
        if (occlusionMap != null) mat.SetTexture("_OcclusionMap", occlusionMap);
        mat.SetFloat("_OcclusionStrength", occlusionStrength);

        // ── Emission ──
        mat.SetTexture("_EmissionMap",  emissionMap);
        mat.SetColor("_EmissionColor",  emissionColor);

        // ── Detail ──
        if (detailMask   != null) mat.SetTexture("_DetailMask",        detailMask);
        if (detailAlbedo != null) mat.SetTexture("_DetailAlbedoMap",   detailAlbedo);
        if (detailNormal != null) mat.SetTexture("_DetailNormalMap",   detailNormal);
        mat.SetFloat("_DetailNormalMapScale", detailNormalScale);

        // ── Misc ──
        mat.SetFloat("_Cutoff", cutoff);
        mat.SetFloat("_SpecularHighlights",    specHighlights);
        mat.SetFloat("_EnvironmentReflections", glossyReflections);
        mat.SetFloat("_Cull", cull);

        // ── Render mode ──
        ApplyRenderMode(mat, renderMode);

        // ── Keywords ──
        Keyword(mat, "_NORMALMAP",                   bumpMap != null);
        Keyword(mat, "_EMISSION",                    emissionEnabled);
        Keyword(mat, "_OCCLUSIONMAP",                occlusionMap != null);
        Keyword(mat, "_PARALLAXMAP",                 parallaxMap != null);
        Keyword(mat, "_METALLICSPECGLOSSMAP",        specular ? specGlossMap != null : metallicGlossMap != null);
        Keyword(mat, "_SPECULAR_SETUP",              specular);
        Keyword(mat, "_DETAIL_MULX2",                detailAlbedo != null || detailNormal != null);
        Keyword(mat, "_SPECULARHIGHLIGHTS_OFF",      specHighlights < 0.5f);
        Keyword(mat, "_ENVIRONMENTREFLECTIONS_OFF",  glossyReflections < 0.5f);
        Keyword(mat, "_ALPHATEST_ON",                renderMode == 1);
        Keyword(mat, "_SURFACE_TYPE_TRANSPARENT",    renderMode >= 2);

        EditorUtility.SetDirty(mat);
        Log($"    OK  (mode={renderMode}, cull={cull})");
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Particle shaders → URP Particles
    // ─────────────────────────────────────────────────────────────────────────

    bool ConvertParticle(Material mat, bool lit, int forceBlend)
    {
        Log($"  → [{mat.name}]  Particle → URP Particles/{(lit ? "Lit" : "Unlit")}");

        // Old Particles/Additive uses _TintColor with implicit ×2; Standard Unlit uses _Color
        Color baseColor;
        if (mat.HasProperty("_TintColor"))
            baseColor = mat.GetColor("_TintColor") * 2f;   // compensate built-in ×2 in shader math
        else
            baseColor = GetColor(mat, "_Color", Color.white);

        Texture mainTex   = GetTex(mat, "_MainTex");
        Vector2 mainScale  = mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex")  : Vector2.one;
        Vector2 mainOffset = mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
        int blendMode      = forceBlend >= 0 ? forceBlend : (int)GetFloat(mat, "_Mode", 0f);

        if (_dryRun) { Log($"    [DRY RUN] blend={blendMode}"); return true; }

        string partShaderName = lit ? _urpPartLit : _urpPartUnlit;
        Shader target = Shader.Find(partShaderName);
        if (target == null) { Log($"    ERROR: '{partShaderName}' not found — check the shader name field at the top of the window."); return false; }

        mat.shader = target;
        mat.SetColor("_BaseColor", baseColor);
        if (mainTex != null)
        {
            mat.SetTexture("_BaseMap", mainTex);
            mat.SetTextureScale("_BaseMap",  mainScale);
            mat.SetTextureOffset("_BaseMap", mainOffset);
        }

        // Particles with forced blend are always transparent
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend",   (float)blendMode);
        ApplyBlendState(mat, blendMode);

        EditorUtility.SetDirty(mat);
        Log($"    OK  (blend={blendMode})");
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Render mode / blend state
    // ─────────────────────────────────────────────────────────────────────────

    // Standard _Mode: 0=Opaque  1=Cutout  2=Fade  3=Transparent
    // URP _Surface:   0=Opaque  1=Transparent
    // URP _Blend:     0=Alpha   1=Premultiply  2=Additive  3=Multiply
    void ApplyRenderMode(Material mat, int mode)
    {
        switch (mode)
        {
            case 0:  // Opaque
                mat.SetFloat("_Surface",   0f);
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.Zero);
                mat.SetFloat("_ZWrite",    1f);
                mat.renderQueue = -1;
                break;

            case 1:  // Cutout
                mat.SetFloat("_Surface",   0f);
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.Zero);
                mat.SetFloat("_ZWrite",    1f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                break;

            case 2:  // Fade → Alpha blend
                mat.SetFloat("_Surface",   1f);
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_Blend",     0f);
                ApplyBlendState(mat, 0);
                break;

            case 3:  // Transparent → Premultiply
                mat.SetFloat("_Surface",   1f);
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_Blend",     1f);
                ApplyBlendState(mat, 1);
                break;
        }
    }

    // URP blend modes: 0=Alpha  1=Premultiply  2=Additive  3=Multiply
    void ApplyBlendState(Material mat, int blend)
    {
        mat.SetFloat("_ZWrite", 0f);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        switch (blend)
        {
            case 0:  // Alpha
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                break;
            case 1:  // Premultiply
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                break;
            case 2:  // Additive
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                break;
            case 3:  // Multiply
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.DstColor);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    static float   GetFloat(Material m, string p, float def)    => m.HasProperty(p) ? m.GetFloat(p)   : def;
    static Color   GetColor(Material m, string p, Color def)    => m.HasProperty(p) ? m.GetColor(p)   : def;
    static Texture GetTex  (Material m, string p)               => m.HasProperty(p) ? m.GetTexture(p) : null;
    static void    Keyword (Material m, string k, bool enabled) { if (enabled) m.EnableKeyword(k); else m.DisableKeyword(k); }

    void Log(string msg) => _log.AppendLine(msg);
}
