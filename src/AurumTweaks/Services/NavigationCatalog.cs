using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>
/// The single, authoritative list of navigable pages for the command palette. Every <see cref="PaletteEntry.Id"/>
/// here MUST be a key that <c>MainViewModel.Navigate</c> understands — this catalog is the palette's half of that
/// contract, and a unit test pins the two in sync so a renamed page key can never silently produce a dead palette
/// row (the honesty mandate: no control that looks like it navigates but goes nowhere). Titles mirror the sidebar
/// labels; <c>Keywords</c> add French synonyms and English aliases so recall doesn't depend on remembering the
/// exact French wording.
/// </summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<PaletteEntry> Pages { get; } = new[]
    {
        Page("Dashboard",      "Tableau de bord",            "Vue d'ensemble", "accueil home overview dashboard resume"),
        Page("Monitoring",     "Monitoring",                 "Vue d'ensemble", "temps reel live capteurs sensors temperatures fps charge usage"),
        Page("Benchmark",      "Benchmark",                  "Vue d'ensemble", "frame times 1% low fps mesure performance test"),
        Page("Tips",           "Conseils",                   "Vue d'ensemble", "recommandations astuces tips guide hardware mods"),
        Page("Journal",        "Journal des modifications",  "Vue d'ensemble", "journal historique history log audit modifications changements trace applique restaure"),
        Page("Snapshots",      "Instantanés système",        "Vue d'ensemble", "instantane snapshot photo etat drift derive regression comparaison comparer reference baseline diff"),

        Page("Tweaks",         "Tweaks Windows",             "Tweaks",         "catalogue registre registry optimisations options"),
        Page("Privacy",        "Confidentialité",            "Tweaks",         "privacy telemetrie telemetry vie privee donnees tracking"),
        Page("Services",       "Services Windows",           "Tweaks",         "services scm demarrage type disable desactiver"),
        Page("VisualEffects",  "Effets visuels",             "Tweaks",         "animations transparence performance ui interface visuel"),
        Page("GameOpti",       "Optimisations jeu",          "Tweaks",         "game mode gpu scheduling hags fse plein ecran fullscreen"),
        Page("ScheduledTasks", "Tâches planifiées",          "Tweaks",         "task scheduler taches planificateur telemetrie cron"),
        Page("Appx",           "Applications préinstallées", "Tweaks",         "debloat bloatware appx uwp store desinstaller uninstall"),

        Page("Bios",           "BIOS",                       "Matériel",       "uefi gros gains mobo carte mere resizable bar pbo curve"),
        Page("Overclocking",   "Overclocking",               "Matériel",       "oc gpu undervolt overclock core clock memory clock afterburner"),
        Page("RamCalc",        "Timings RAM",                "Matériel",       "ram calculatrice timings cl tcl trcd ddr4 ddr5 dram"),
        Page("MemoryModules",  "Barrettes mémoire",          "Matériel",       "ram modules barrettes spd xmp expo dimm slots kit"),
        Page("Drivers",        "Pilotes",                    "Matériel",       "drivers ddu nvcleanstall nvidia amd gpu installer"),
        Page("Devices",        "Périphériques",              "Matériel",       "input souris mouse clavier keyboard polling rate usb manette"),
        Page("Display",        "Affichage",                  "Matériel",       "ecran monitor frequence refresh hz hdr resolution moniteur"),
        Page("Audio",          "Son",                        "Matériel",       "audio son peripheriques exclusif latence sortie sound"),

        Page("NetworkAdapters","Cartes réseau",              "Réseau",         "network adapters nic ethernet wifi carte reseau nagle rss"),
        Page("Dns",            "Serveurs DNS",               "Réseau",         "dns resolution cloudflare google quad9 adguard resolveur"),

        Page("Stability",      "Stabilité RAM",              "Stabilité",      "memtest stabilite memoire ram test stress erreurs"),
        Page("CpuStability",   "Stabilité CPU",              "Stabilité",      "cpu stress test prime95 occt stabilite processeur charge"),
        Page("Latency",        "Latence DPC/ISR",            "Stabilité",      "latency dpc isr latencymon micro saccades stutter interruptions"),
        Page("DriveHealth",    "Santé des disques",          "Stabilité",      "smart disque ssd hdd nvme sante health usure tbw"),

        Page("Memory",         "Mémoire vive",               "Mémoire",        "ram memoire standby cache vider purge superfetch"),
        Page("Pagefile",       "Mémoire virtuelle",          "Mémoire",        "pagefile swap fichier echange virtuelle taille"),

        Page("Startup",        "Démarrage",                  "Système",        "startup demarrage autorun programmes boot lancement"),
        Page("Power",          "Alimentation",               "Système",        "power plan alimentation haute performance ultimate energie"),
        Page("SleepHib",       "Veille & hibernation",       "Système",        "veille hibernation sommeil sleep hiberfil fast startup"),
        Page("DiskCleanup",    "Nettoyage disque",           "Système",        "nettoyage temp cache cleanup disque espace fichiers temporaires"),
        Page("ProcessControl", "Priorité & affinité",        "Système",        "process priorite affinity affinite cpu coeurs taskmgr"),
        Page("WindowsUpdate",  "Windows Update",             "Système",        "windows update mise a jour wu pause differer"),
        Page("RestorePoints",  "Points de restauration",     "Système",        "restore point restauration systeme sauvegarde snapshot"),

        Page("Gaming",         "Gaming",                     "Jeu",            "gaming jeux game mode detection steam epic riot reseau"),

        Page("Profiles",       "Profils",                    "Profils",        "profils presets profiles stock competitif streaming extreme"),
        Page("Settings",       "Paramètres",                 "Application",    "settings parametres langue restauration telemetrie options"),
        Page("Transparency",   "Transparence & confiance",   "Application",    "transparence confiance trust honnete honesty privacy confidentialite telemetrie reseau serveur reversible integrite ce que fait ne fait pas limites avis securite"),
        Page("License",        "Licence",                    "Application",    "licence license premium activation activer cle key edition gratuite payante achat acheter debloquer"),
    };

    private static PaletteEntry Page(string key, string title, string group, string keywords)
        => new(key, title, group, PaletteEntryKind.Page, keywords);
}
