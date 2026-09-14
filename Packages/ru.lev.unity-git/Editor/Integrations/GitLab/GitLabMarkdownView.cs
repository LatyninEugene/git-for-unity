using System;
using System.Collections.Generic;
using Lev.Git.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Текст из GitLab с картинками: описание задачи или MR, комментарий.
    ///
    /// Разметка показывается как есть, кроме картинок — вместо
    /// «![скриншот](/uploads/…)» видна сама картинка. Загружается она через
    /// API с токеном: у закрытого проекта файлы без входа не отдаются. Картинки
    /// с других сайтов не загружаются вовсе — токен туда уйти не должен, а
    /// сторонний сервер не должен узнавать, кто и когда открыл задачу.
    /// </summary>
    public sealed class GitLabMarkdownView : VisualElement
    {
        private const int CacheLimit = 40;
        private const float MaxImageHeight = 420f;

        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static readonly List<string> CacheOrder = new List<string>();

        public GitLabMarkdownView(string markdown, Func<GitLabClient> client, string textClass = "banner__text", int maxChars = 8000)
        {
            var text = markdown ?? string.Empty;
            if (text.Length > maxChars) text = text.Substring(0, maxChars) + "\n…";

            foreach (var segment in MarkdownImages.Split(text))
            {
                if (!segment.IsImage)
                {
                    var label = Ui.Text(segment.Text, textClass);
                    label.style.whiteSpace = WhiteSpace.Normal;
                    label.selection.isSelectable = true;
                    Add(label);
                    continue;
                }

                Add(ImageBlock(segment, client));
            }
        }

        private static VisualElement ImageBlock(MdSegment segment, Func<GitLabClient> client)
        {
            var web = MarkdownImages.Resolve(segment.ImageUrl, GitLabInstance.EffectiveProjectWebUrl, GitLabInstance.EffectiveBaseUrl);
            var alt = string.IsNullOrEmpty(segment.ImageAlt) ? L.T("image") : segment.ImageAlt;

            var box = new VisualElement();
            box.style.marginTop = 4f;
            box.style.marginBottom = 4f;

            var status = Ui.Text(L.F("Loading “{0}”…", alt), "note__meta");
            box.Add(status);

            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
            image.style.display = DisplayStyle.None;
            image.style.alignSelf = Align.FlexStart;
            image.tooltip = L.F("{0} — click to open in the browser", alt);
            image.RegisterCallback<ClickEvent>(_ => Application.OpenURL(web));
            image.RegisterCallback<GeometryChangedEvent>(_ => Fit(image));
            box.Add(image);

            Load(image, status, web, alt, client);
            return box;
        }

        private static async void Load(Image image, Label status, string web, string alt, Func<GitLabClient> client)
        {
            if (Cache.TryGetValue(web, out var cached) && cached != null)
            {
                Show(image, status, cached);
                return;
            }

            if (!MarkdownImages.SameOrigin(web, GitLabInstance.EffectiveBaseUrl))
            {
                Fail(status, web, L.F("“{0}” is an image from another site and is not loaded in the editor. Click to open it in the browser.", alt));
                return;
            }

            var c = client != null ? client() : null;
            if (c == null)
            {
                Fail(status, web, L.F("“{0}” was not loaded: the API token is not set. Click to open it in the browser.", alt));
                return;
            }

            GitLabBytes r = null;
            try
            {
                var api = MarkdownImages.UploadApiPath(web, GitLabInstance.EffectiveProjectWebUrl, GitLabInstance.EffectiveEncodedId);
                if (api != null) r = await c.GetBytesAsync(api);

                // Старый GitLab без скачивания загрузок через API — пробуем прямой адрес.
                if (r == null || !r.Ok) r = await c.GetBytesAsync(web);
            }
            catch (Exception e)
            {
                r = new GitLabBytes { Error = e.Message };
            }

            Texture2D tex = null;
            if (r.Ok && r.Data != null && r.Data.Length > 0)
            {
                tex = new Texture2D(2, 2);
                if (!tex.LoadImage(r.Data))
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    tex = null;
                }
            }

            if (tex == null)
            {
                Fail(status, web, L.F("“{0}” was not loaded: {1}. Click to open it in the browser.", alt,
                                      r.Ok ? L.T("the server returned something other than an image — this GitLab may serve files only after signing in in the browser") : r.Error));
                return;
            }

            tex.name = alt;
            Remember(web, tex);
            Show(image, status, tex);
        }

        private static void Show(Image image, Label status, Texture2D tex)
        {
            status.style.display = DisplayStyle.None;
            image.image = tex;
            image.style.display = DisplayStyle.Flex;
            image.style.width = Length.Percent(100);
            image.style.maxWidth = tex.width;
            Fit(image);
        }

        /// <summary>Высота по ширине: картинка во всю ширину карточки, но не крупнее своего размера.</summary>
        private static void Fit(Image image)
        {
            var tex = image.image;
            if (tex == null || tex.width <= 0) return;

            float width = image.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 0f) return;

            float height = Mathf.Min(width * tex.height / tex.width, MaxImageHeight);
            if (Mathf.Abs(image.resolvedStyle.height - height) > 0.5f) image.style.height = height;
        }

        private static void Fail(Label status, string web, string text)
        {
            status.text = text;
            status.style.whiteSpace = WhiteSpace.Normal;
            status.RegisterCallback<ClickEvent>(_ => Application.OpenURL(web));
        }

        private static void Remember(string key, Texture2D tex)
        {
            Cache[key] = tex;
            CacheOrder.Remove(key);
            CacheOrder.Add(key);

            while (CacheOrder.Count > CacheLimit)
            {
                var oldest = CacheOrder[0];
                CacheOrder.RemoveAt(0);
                if (Cache.TryGetValue(oldest, out var old) && old != null) UnityEngine.Object.DestroyImmediate(old);
                Cache.Remove(oldest);
            }
        }
    }
}
