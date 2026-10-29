using System.Globalization;
using System.Text.RegularExpressions;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Catalog;

// Our production lines cannot run polyamide-based blends, so such products are refused before they enter the
// catalogue or reach a marketplace.
public static partial class ProductionRules
{
    public const string PolyamideMessage = "Üretim hatlarımız gereği polyamid bazlı karışımlarla üretim gerçekleştiremiyoruz; bu nedenle belirtilen spesifikasyondaki ürünü maalesef mevcut standartlarımızda üretememekteyiz.";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Name, category and fabric attributes: polyamide under any of its names (nylon/naylon, PA6/PA66).</summary>
    public static bool CompositionHasPolyamide(params string?[] texts) => texts.Any(t => !string.IsNullOrEmpty(t) && Composition().IsMatch(t.ToLower(Turkish)));

    /// <summary>Descriptions and free text: only the word polyamide itself, since "naylon" there usually means packaging.</summary>
    public static bool TextHasPolyamide(params string?[] texts) => texts.Any(t => !string.IsNullOrEmpty(t) && FreeText().IsMatch(t.ToLower(Turkish)));

    public static void EnsureProducible(IEnumerable<string?> composition, IEnumerable<string?> freeText)
    {
        if (CompositionHasPolyamide(composition.ToArray()) || TextHasPolyamide(freeText.ToArray()))
            throw new IntegrationException("production_rule", PolyamideMessage, 422);
    }

    // Lower-cased with Turkish rules, so POLİAMİD, Poliamid and POLYAMIDE (→ polyamıde) all match.
    [GeneratedRegex(@"pol[iıy]am[iı]d|n[ay]?ylon|\bpa ?6(?:6|\.6)?\b")]
    private static partial Regex Composition();

    [GeneratedRegex(@"pol[iıy]am[iı]d")]
    private static partial Regex FreeText();
}
