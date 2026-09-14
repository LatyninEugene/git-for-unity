using System.Runtime.CompilerServices;

// Поддержка Timeline — отдельная сборка: она собирается, только если пакет
// Timeline установлен, и ей нужны внутренние типы системы превью.
[assembly: InternalsVisibleTo("Lev.Git.Timeline.Editor")]

// Интеграция GitLab показывает миниатюры версий в обсуждениях merge request.
[assembly: InternalsVisibleTo("Lev.Git.GitLab.Editor")]
