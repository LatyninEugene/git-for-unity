using System;
using System.Collections.Generic;
using System.Text;

namespace Lev.Git.UI
{
    /// <summary>Папка в дереве файлов.</summary>
    public sealed class PathNode<T>
    {
        /// <summary>Что показываем: после сжатия это может быть «a/b/c».</summary>
        public string Name;

        /// <summary>Полный путь папки — ключ для запоминания свёрнутости.</summary>
        public string Path;

        public readonly List<PathNode<T>> Folders = new List<PathNode<T>>();
        public readonly List<T> Files = new List<T>();

        /// <summary>
        /// Изменение самой папки — в Unity это её .meta. Показывается в строке
        /// папки, а не отдельной строкой-двойником рядом с ней.
        /// </summary>
        public T Self;
        public bool HasSelf;

        /// <summary>Все изменения поддерева: мета самой папки, вложенные папки, файлы.</summary>
        public void Collect(List<T> into)
        {
            if (HasSelf) into.Add(Self);
            foreach (var f in Folders) f.Collect(into);
            into.AddRange(Files);
        }
    }

    /// <summary>
    /// Раскладка плоского списка путей по папкам.
    ///
    /// Общая для списка изменений и для файлов коммита: обе панели показывают
    /// одно и то же — набор путей, — и дерево в них обязано вести себя
    /// одинаково, вплоть до того, какие цепочки папок схлопываются.
    /// </summary>
    public static class PathTree
    {
        /// <param name="isFolder">
        /// Элемент описывает саму папку, а не файл в ней. Такой элемент
        /// становится <see cref="PathNode{T}.Self"/> узла с его путём.
        /// </param>
        public static PathNode<T> Build<T>(IEnumerable<T> items, Func<T, string> pathOf,
                                           Func<T, bool> isFolder = null)
        {
            var root = new PathNode<T> { Name = string.Empty, Path = string.Empty };

            foreach (var item in items)
            {
                var path = pathOf(item);
                bool folder = isFolder != null && isFolder(item);

                // У папки узлом служит она сама, у файла — его каталог.
                var node = Descend(root, folder ? path : Ui.DirOf(path));

                if (folder && node != root)
                {
                    node.Self = item;
                    node.HasSelf = true;
                }
                else
                {
                    node.Files.Add(item);
                }
            }

            Compress(root);
            return root;
        }

        private static PathNode<T> Descend<T>(PathNode<T> root, string dir)
        {
            var node = root;
            if (string.IsNullOrEmpty(dir)) return node;

            var acc = new StringBuilder();
            foreach (var part in dir.Split('/'))
            {
                if (acc.Length > 0) acc.Append('/');
                acc.Append(part);
                var path = acc.ToString();

                PathNode<T> child = null;
                foreach (var f in node.Folders)
                    if (f.Path == path) { child = f; break; }

                if (child == null)
                {
                    child = new PathNode<T> { Name = part, Path = path };
                    node.Folders.Add(child);
                }
                node = child;
            }

            return node;
        }

        /// <summary>
        /// Схлопывает цепочки папок без развилок.
        ///
        /// Packages/ru.lev.unity-git/Editor/UI показывается одной строкой,
        /// а не четырьмя: в Unity-проекте такие цепочки — норма, и без сжатия
        /// дерево уезжает вправо, ничего не сообщая по дороге.
        ///
        /// Папку с собственным изменением не схлопываем с дочерней: её мета
        /// показывается в её строке, и строка должна остаться.
        /// </summary>
        private static void Compress<T>(PathNode<T> node)
        {
            for (int i = 0; i < node.Folders.Count; i++)
            {
                var f = node.Folders[i];
                Compress(f);

                while (f.Files.Count == 0 && f.Folders.Count == 1 && !f.HasSelf)
                {
                    var only = f.Folders[0];
                    var merged = new PathNode<T>
                    {
                        Name = f.Name + "/" + only.Name,
                        Path = only.Path,
                        Self = only.Self,
                        HasSelf = only.HasSelf
                    };
                    merged.Folders.AddRange(only.Folders);
                    merged.Files.AddRange(only.Files);
                    f = merged;
                }

                node.Folders[i] = f;
            }

            node.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
