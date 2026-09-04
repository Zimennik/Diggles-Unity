#nullable enable

namespace Diggles.Runtime.UI
{
    using Diggles.Runtime.Localization;
    using UnityEngine;
    using UnityEngine.UI;

    /// <summary>
    /// Drop-on localized art: add next to a uGUI <see cref="Image"/>, <see cref="RawImage"/> or
    /// a <see cref="SpriteRenderer"/> and point <see cref="PathPattern"/> at a data-relative
    /// file — plain (<c>GUI/Karte_a.tga</c>) or locale-tokened (<c>GUI/logo_{locale}.tga</c>,
    /// resolution: current locale → en → token stripped; see LocalizedAssetLibrary). The art
    /// re-resolves live on locale switch. PNG/JPG user art works alongside the shipped TGAs.
    /// </summary>
    [ExecuteAlways]   // same law as LocalizedText: the Changed subscription must exist in
                      // EditMode (headless suites + editor preview).
    [DisallowMultipleComponent]
    [AddComponentMenu("Diggles/Localized Sprite")]
    public sealed class LocalizedSprite : MonoBehaviour
    {
        [Tooltip("Data-relative path, optionally with {locale}: e.g. 'GUI/logo_{locale}.tga'.")]
        [SerializeField] string pathPattern = "";

        [Tooltip("Resize the Image to the source texture dimensions after assignment.")]
        [SerializeField] bool setNativeSize;

        Image? _image;
        RawImage? _rawImage;
        SpriteRenderer? _spriteRenderer;
        bool _missingTargetWarned;

        public string PathPattern
        {
            get => pathPattern;
            set { pathPattern = value; Apply(); }
        }

        void OnEnable()
        {
            GameLocalization.Changed += Apply;
            Apply();
        }

        void OnDisable() => GameLocalization.Changed -= Apply;

        void Apply()
        {
            if (GameLocalization.Current is not { } localization || string.IsNullOrEmpty(pathPattern)) return;
            if (!ResolveTarget())
            {
                if (!_missingTargetWarned)
                    Debug.LogWarning($"[LocalizedSprite] no Image/RawImage/SpriteRenderer on '{name}'", this);
                _missingTargetWarned = true;
                return;
            }

            if (_rawImage != null)
            {
                var texture = localization.Assets.GetTexture(pathPattern);
                if (texture == null) return;
                _rawImage.texture = texture;
                if (setNativeSize) _rawImage.SetNativeSize();
                return;
            }

            var sprite = localization.Assets.GetSprite(pathPattern);
            if (sprite == null) return;
            if (_image != null)
            {
                _image.sprite = sprite;
                if (setNativeSize) _image.SetNativeSize();
            }
            else if (_spriteRenderer != null)
            {
                _spriteRenderer.sprite = sprite;
            }
        }

        bool ResolveTarget()
        {
            if (_image != null || _rawImage != null || _spriteRenderer != null) return true;
            return TryGetComponent(out _image)
                   || TryGetComponent(out _rawImage)
                   || TryGetComponent(out _spriteRenderer);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Application.isPlaying && isActiveAndEnabled) Apply();
        }
#endif
    }
}
