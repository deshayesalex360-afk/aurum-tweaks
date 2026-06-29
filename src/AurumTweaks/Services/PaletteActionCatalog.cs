using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>
/// The global actions the command palette can <em>run</em> (not just navigate to) — the "command" half of a command
/// palette. Each <see cref="PaletteEntry.Id"/> here MUST be handled by <c>MainViewModel.RunPaletteAction</c>, and a
/// unit test pins the two in sync so a palette action can never look runnable but do nothing (the honesty mandate:
/// no dead control). Scope is deliberately limited to safe, self-describing actions — export/copy a report, export
/// the journal — never a one-keystroke system mutation, which must stay on its page where the user sees the context.
/// Ids are PascalCase and deliberately disjoint from page keys so the navigation handler can dispatch on
/// <see cref="PaletteEntry.Kind"/> without ambiguity.
/// </summary>
public static class PaletteActionCatalog
{
    public static IReadOnlyList<PaletteEntry> Actions { get; } = new[]
    {
        Action("ExportSystemReport", "Exporter le rapport système", "rapport systeme export fichier txt partager forum support materiel hardware sauvegarder"),
        Action("CopySystemReport",   "Copier le rapport système",   "rapport systeme copier presse papiers clipboard coller partager forum discord support"),
        Action("ExportJournal",      "Exporter le journal",         "journal export historique fichier txt audit modifications partager support trace"),
        Action("CopyJournal",        "Copier le journal",           "journal copier presse papiers clipboard coller historique audit partager forum discord trace"),
        Action("ExportEvidenceReport", "Exporter la preuve avant/après", "preuve evidence avant apres before after gain fps benchmark instantane snapshot score comparaison export fichier txt partager forum"),
        Action("CopyEvidenceReport",   "Copier la preuve avant/après",   "preuve evidence avant apres before after gain fps benchmark instantane snapshot score comparaison copier presse papiers clipboard coller forum discord"),
        Action("ExportTransparency",   "Exporter la transparence",       "transparence confiance trust honnete privacy confidentialite telemetrie reversible integrite ce que fait ne fait pas limites export fichier txt partager avis"),
        Action("CopyTransparency",     "Copier la transparence",         "transparence confiance trust honnete privacy confidentialite telemetrie reversible integrite ce que fait ne fait pas limites copier presse papiers clipboard coller forum discord avis"),
    };

    private static PaletteEntry Action(string id, string title, string keywords)
        => new(id, title, "Action", PaletteEntryKind.Action, keywords);
}
