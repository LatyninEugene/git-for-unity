using UnityEditor;

namespace Lev.Git
{
    /// <summary>
    /// Настройки ядра git. Лежат в ProjectSettings/, то есть коммитятся и общие
    /// для команды — секретов здесь нет. У каждой интеграции свои настройки в
    /// своём файле: ядро о хостингах не знает.
    /// </summary>
    [FilePath(GitSettings.AssetPath, FilePathAttribute.Location.ProjectFolder)]
    public sealed class GitSettings : ScriptableSingleton<GitSettings>
    {
        public const string AssetPath = "ProjectSettings/LevGitSettings.asset";

        /// <summary>Remote, который считается основным: для push без upstream и для интеграций.</summary>
        public string remoteName = "origin";

        /// <summary>
        /// Разрешить отмечать к коммиту отдельные строки, а не только фрагменты.
        /// По умолчанию выключено: нужно редко, а места в diff занимает много.
        /// </summary>
        public bool lineLevelSelection;

        /// <summary>
        /// Показывать изменения открытой сцены в окне Hierarchy и в инспекторе.
        /// Пересчёт разбирает YAML сцены целиком — на очень больших сценах можно выключить.
        /// </summary>
        public bool sceneChangeIndicators = true;

        /// <summary>Показывать в инспекторе, кто последним менял объект.</summary>
        public bool objectBlame;

        /// <summary>
        /// Автолок при первой правке: 0 — выключен, 1 — только сцены,
        /// 2 — все файлы, помеченные lockable.
        /// </summary>
        public int autoLock;

        /// <summary>
        /// Что делать при сохранении файла, помеченного lockable, на который нет
        /// лока (git-lfs держит его только для чтения): 0 — спрашивать,
        /// 1 — брать лок, 2 — сохранять без лока, сняв «только для чтения».
        /// </summary>
        public int readOnlySave;

        /// <summary>
        /// Фон превью ассетов: 0 — тёмный, 1 — серый, 2 — светлый, 3 — шахматка,
        /// 4 — свой цвет. Командная настройка: тёмные спрайты на тёмном не
        /// видны, светлые — на светлом, и выбирает это проект, а не каждый себе.
        /// </summary>
        public int previewBackground;

        /// <summary>Цвет фона превью для варианта «свой цвет».</summary>
        public UnityEngine.Color previewBackgroundColor = new UnityEngine.Color(0.2f, 0.25f, 0.3f);

        /// <summary>Шахматка под картинками с прозрачностью — чтобы прозрачные места было видно.</summary>
        public bool previewCheckerUnderImages = true;

        /// <summary>
        /// Временный импорт прошлых версий моделей, PSD, EXR и других ассетов,
        /// которые Unity читает только импортом: 0 — выключен, 1 — по кнопке,
        /// 2 — всегда. Импорт трогает папку Assets, поэтому это решение проекта.
        /// </summary>
        public int importSandbox = 1;

        /// <summary>Откуда брать ассеты, на которые ссылается прошлая версия: 0 — из проекта, 1 — из того же коммита.</summary>
        public int previewDependencies;

        /// <summary>
        /// Миниатюры версий вместо иконок в списках файлов: 0 — выключены,
        /// 1 — только картинки, 2 — все типы (материалы, модели — рисуются дольше).
        /// </summary>
        public int listThumbnails = 1;

        /// <summary>
        /// Сколько места могут занять временный импорт, миниатюры и временные версии
        /// вместе, МБ. Больше — старое удаляется само. Резервные копии слияний не считаются.
        /// </summary>
        public int previewStorageLimitMb = 1024;

        public void Persist()
        {
            Save(true);
        }
    }
}
