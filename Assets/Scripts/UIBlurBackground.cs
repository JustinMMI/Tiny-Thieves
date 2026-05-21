using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Injects a full-panel blur background as the first child of any UI panel.
/// Requires the "Custom/UIBlur" shader and Opaque Texture enabled on the URP asset.
/// Attach this component to the root GameObject of any screen panel (e.g., LoseScreen, WinScreen).
/// </summary>
[DisallowMultipleComponent]
public sealed class UIBlurBackground : MonoBehaviour
{
    private const string ShaderName         = "Custom/UIBlur";
    private const string BackgroundChildName = "BlurBackground";

    [Header("Blur Settings")]
    [SerializeField, Range(0.5f, 10f)] private float blurSize   = 4f;
    [SerializeField, Range(1,    16)]  private int   iterations = 8;

    [Header("Tint (RGB = colour, A = opacity)")]
    [SerializeField] private Color tint = new Color(0f, 0f, 0f, 0.35f);

    private Material _material;

    private void Awake()
    {
        _material = CreateMaterial();
        EnsureBackground();
    }

    private void OnValidate()
    {
        // Live-update while tweaking values in the Inspector.
        if (_material != null)
        {
            ApplyProperties(_material);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>Creates (or re-uses) the per-instance blur material.</summary>
    private Material CreateMaterial()
    {
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[UIBlurBackground] Shader '{ShaderName}' not found. " +
                           "Make sure UIBlur.shader is in the project.", this);
            return null;
        }

        Material mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        ApplyProperties(mat);
        return mat;
    }

    /// <summary>Pushes Inspector values onto the material.</summary>
    private void ApplyProperties(Material mat)
    {
        mat.SetFloat("_BlurSize",   blurSize);
        mat.SetInt  ("_Iterations", iterations);
        mat.SetColor("_Tint",       tint);
    }

    /// <summary>
    /// Creates a child RawImage that fills the panel and uses the blur material,
    /// then ensures it sits behind all other children (sibling index 0).
    /// </summary>
    private void EnsureBackground()
    {
        if (_material == null)
        {
            return;
        }

        // Re-use an existing background if the component is re-initialised.
        Transform existing = transform.Find(BackgroundChildName);
        if (existing != null)
        {
            RawImage ri = existing.GetComponent<RawImage>();
            if (ri != null)
            {
                ri.material = _material;
            }

            existing.SetSiblingIndex(0);
            return;
        }

        // Create the background GameObject.
        GameObject go = new GameObject(BackgroundChildName, typeof(RectTransform));
        go.transform.SetParent(transform, false);
        go.transform.SetSiblingIndex(0);

        // Stretch to fill the parent panel.
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // RawImage with the blur material — no texture needed, the shader
        // samples _CameraOpaqueTexture in screen space directly.
        RawImage rawImage = go.AddComponent<RawImage>();
        rawImage.material = _material;
        rawImage.color    = Color.white;
    }

    private void OnDestroy()
    {
        if (_material != null)
        {
            Destroy(_material);
        }
    }
}
