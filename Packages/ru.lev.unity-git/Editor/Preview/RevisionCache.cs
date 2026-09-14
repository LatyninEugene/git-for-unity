using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Загруженные версии, общие для всех панелей.
    ///
    /// Ключ — объект git и путь файла. Git адресует содержимое, поэтому один и
    /// тот же материал в десяти коммитах — один объект: листая историю, его не
    /// читают из git и не собирают заново. Путь в ключе потому, что по нему
    /// загрузчик выбирает главный объект.
    ///
    /// Панели получают версию по счётчику ссылок и возвращают через Release.
    /// Несколько последних неиспользуемых версий держатся на случай возврата,
    /// остальные уничтожаются.
    /// </summary>
    internal static class RevisionCache
    {
        private const int KeepUnused = 16;

        private sealed class Entry
        {
            public LoadedAsset Asset;
            public int Refs;
            public long Used;
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<LoadedAsset, string> Keys = new Dictionary<LoadedAsset, string>();
        private static long _clock;

        static RevisionCache()
        {
            // Сами объекты уничтожит LoadedAsset перед перезагрузкой домена.
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Entries.Clear();
                Keys.Clear();
            };
        }

        /// <summary>Готовая версия по ключу — со ссылкой, которую надо вернуть через Release. null — её нет.</summary>
        public static LoadedAsset Acquire(string key)
        {
            Entry entry;
            if (key == null || !Entries.TryGetValue(key, out entry)) return null;

            // Объекты могли уничтожить снаружи — такая запись больше не годится.
            if (entry.Asset.Main == null)
            {
                Entries.Remove(key);
                Keys.Remove(entry.Asset);
                return null;
            }

            entry.Refs++;
            entry.Used = ++_clock;
            return entry.Asset;
        }

        /// <summary>Кладёт только что загруженную версию; ссылка на неё уже считается выданной.</summary>
        public static void Add(string key, LoadedAsset asset)
        {
            if (key == null || asset == null || asset.Main == null || asset.Live) return;

            // Две панели грузили одно и то же одновременно: в кэше остаётся
            // первая версия, вторая живёт сама и уничтожится при возврате.
            if (Entries.ContainsKey(key) || Keys.ContainsKey(asset)) return;

            Entries[key] = new Entry { Asset = asset, Refs = 1, Used = ++_clock };
            Keys[asset] = key;
        }

        /// <summary>Возвращает ссылку. Версия не из кэша уничтожается сразу.</summary>
        public static void Release(LoadedAsset asset)
        {
            if (asset == null) return;

            string key;
            if (!Keys.TryGetValue(asset, out key))
            {
                asset.Dispose();
                return;
            }

            var entry = Entries[key];
            entry.Refs = Math.Max(0, entry.Refs - 1);
            entry.Used = ++_clock;

            Trim();
        }

        private static void Trim()
        {
            var unused = Entries.Where(p => p.Value.Refs == 0).OrderBy(p => p.Value.Used).ToList();

            for (int i = 0; i < unused.Count - KeepUnused; i++)
            {
                Entries.Remove(unused[i].Key);
                Keys.Remove(unused[i].Value.Asset);
                unused[i].Value.Asset.Dispose();
            }
        }
    }
}
