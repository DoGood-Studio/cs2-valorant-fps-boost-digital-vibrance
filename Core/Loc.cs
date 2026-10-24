using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace GameColorGuard.Core;

/// <summary>
/// Arayüz dili. Kaynak metinler Türkçedir; Data/Lang/keys.json her metne bir kimlik verir, xx.json o kimliğin çevirisini
/// tutar (eksikse İngilizceye, o da yoksa Türkçeye düşer). "{0}" içeren metinler kalıptır: ekrandaki birleşik cümle
/// kalıpla eşleşir, değişken kısımlar da ayrıca çevrilir. Ekrandaki öğelerin Türkçe aslı saklanır, böylece dil
/// yeniden başlatmadan değiştirilebilir.
/// </summary>
internal static class Loc
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("tr", "Türkçe"), ("en", "English"), ("ru", "Русский"), ("ar", "العربية"),
        ("de", "Deutsch"), ("es", "Español"), ("pt", "Português")
    };

    private static readonly string[] Separators = { "  ·  ", " · ", ", ", "\n" };
    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static List<Pattern> _patterns = new();
    private static readonly ConditionalWeakTable<DependencyObject, Original> Store = new();
    // Hiç başka dile geçilmediyse ağaç dolaşılmaz, asıllar saklanmaz (Türkçe kullanımda ek bellek yok).
    private static bool _everTranslated;

    /// <summary>"{0}" içeren kalıp; sabit baş/son kısım regex'ten önce ucuzca denetlenir.</summary>
    private sealed record Pattern(Regex Regex, string Prefix, string Suffix, string Target);

    public static string Language { get; private set; } = "tr";
    public static bool IsSource => Language == "tr";

    // Arapçada ar-SA Hicri takvim kullanır; tarihler Miladi kalsın diye ar-AE.
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language switch
    {
        "en" => "en-US", "ru" => "ru-RU", "ar" => "ar-AE", "de" => "de-DE", "es" => "es-ES", "pt" => "pt-BR", _ => "tr-TR"
    });

    /// <summary>Kayıtlı dil; yoksa (ilk açılış) İngilizce.</summary>
    public static void Init(string? preferred) => Set(Supported(preferred?.ToLowerInvariant()) ?? "en");

    /// <summary>
    /// Dil seçimi kaydedilmemiş eski kurulumlar Windows diliyle açılıyordu; aynı dilde kalsınlar diye o dil kaydedilir.
    /// </summary>
    public static string LegacyDefault() => Supported(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? "en";

    private static string? Supported(string? code) => Languages.Any(l => l.Code == code) ? code : null;

    public static void Set(string code)
    {
        Language = Languages.Any(l => l.Code == code) ? code : "en";
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<Pattern>();
        if (!IsSource)
        {
            _everTranslated = true;
            Dictionary<string, string> keys = Load("keys");
            Dictionary<string, string> target = Load(Language);
            Dictionary<string, string> english = Language == "en" ? target : Load("en");
            foreach ((string id, string source) in keys)
            {
                string? text = target.TryGetValue(id, out string? t) ? t : english.TryGetValue(id, out string? e) ? e : null;
                if (string.IsNullOrEmpty(text)) continue;
                exact.TryAdd(source, text);
                if (source.Contains("{0}", StringComparison.Ordinal))
                {
                    int first = source.IndexOf('{'), last = source.LastIndexOf('}');
                    patterns.Add(new Pattern(ToRegex(source), source[..first], source[(last + 1)..], text));
                }
            }
            // Sabit metni daha uzun (daha belirli) kalıplar önce denenir: "Şu an: {0}", genel "{0}: {1}"den önce.
            patterns.Sort((a, b) => Literal(b).CompareTo(Literal(a)));
            static int Literal(Pattern p) => Regex.Replace(p.Regex.ToString(), @"\(\?<p\d+>\.\*\?\)|\\", "").Length;
        }
        _exact = exact;
        _patterns = patterns;
    }

    /// <summary>Tüm Türkçe kaynak metinler (öz-test için).</summary>
    internal static IReadOnlyCollection<string> Sources() => Load("keys").Values.ToHashSet(StringComparer.Ordinal);

    /// <summary>Türkçe metnin seçili dildeki karşılığı (bulunamazsa olduğu gibi).</summary>
    public static string T(string? text) => text is null ? "" : Translate(text, 0);

    /// <summary>
    /// Aynı Türkçe metin bağlama göre farklı çevrildiğinde ("Kapalı": oyun kapalı / ayar kapalı) "bağlam|metin"
    /// anahtarı kullanılır; yoksa düz çeviriye düşer.
    /// </summary>
    public static string T(string text, string context) =>
        IsSource ? text : _exact.TryGetValue(context + "|" + text, out string? hit) ? hit : T(text);

    /// <summary>Türkçe kalıbı çevirip doldurur.</summary>
    public static string F(string template, params object?[] args) => string.Format(Culture, T(template), args);

    private static string Translate(string text, int depth)
    {
        if (IsSource || text.Length == 0) return text;
        if (_exact.TryGetValue(text, out string? hit)) return hit;
        string trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0 && _exact.TryGetValue(trimmed, out string? trimmedHit))
            return text.Replace(trimmed, trimmedHit);
        foreach (Pattern pattern in _patterns)
        {
            if (!text.StartsWith(pattern.Prefix, StringComparison.Ordinal) || !text.EndsWith(pattern.Suffix, StringComparison.Ordinal)) continue;
            Match match = pattern.Regex.Match(text);
            if (!match.Success) continue;
            var values = new List<object?>();
            for (int i = 0; match.Groups["p" + i].Success; i++)
                values.Add(depth < 3 ? Translate(match.Groups["p" + i].Value, depth + 1) : match.Groups["p" + i].Value);
            try { return string.Format(Culture, pattern.Target, values.ToArray()); }
            catch (FormatException) { return text; }
        }
        // Birleşik metin ("Dizüstü · Prizde", "a, b"): parçaları ayrı ayrı çevir.
        if (depth < 3)
        {
            foreach (string separator in Separators)
            {
                if (!text.Contains(separator, StringComparison.Ordinal)) continue;
                string[] parts = text.Split(separator);
                string[] translated = parts.Select(p => Translate(p, depth + 1)).ToArray();
                if (!parts.SequenceEqual(translated)) return string.Join(separator, translated);
            }
        }
        return text;
    }

    private static Regex ToRegex(string template)
    {
        string escaped = Regex.Escape(template);
        // Boş değer de eşleşir: "{0}{1}" gibi bitişik yer tutucularda birim boş olabilir.
        string pattern = Regex.Replace(escaped, @"\\\{(\d+)}", m => $"(?<p{m.Groups[1].Value}>.*?)");
        return new Regex("^" + pattern + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static Dictionary<string, string> Load(string name)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lang." + name);
            return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Write("Dil dosyası okunamadı: " + name, ex);
            return new();
        }
    }

    // ------------------------------------------------------------------ Arayüz ağacı

    private sealed class Original
    {
        public string? Text, TextShown, Tip, TipShown, Name, NameShown;
    }

    /// <summary>Metni Türkçe aslıyla kaydedip seçili dilde gösterir (dil değişince yeniden çevrilebilir).</summary>
    public static void SetText(TextBlock block, string source)
    {
        if (!_everTranslated)
        {
            block.Text = source;
            return;
        }
        Original o = Store.GetOrCreateValue(block);
        o.Text = source;
        o.TextShown = T(source);
        block.Text = o.TextShown;
    }

    /// <summary>
    /// Ağacı dolaşıp TextBlock/Run metinlerini, ipuçlarını ve erişilebilirlik adlarını çevirir. Koddan sonradan
    /// değiştirilmiş bir metin yeni Türkçe asıl sayılır. Veri bağlamalı metinlere ve yazı kutularına dokunulmaz.
    /// </summary>
    public static void Translate(DependencyObject root)
    {
        if (!_everTranslated) return;
        var visited = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (!visited.Add(node) || node is TextBox) continue;

            if (node is TextBlock block && !BindingOperations.IsDataBound(block, TextBlock.TextProperty))
            {
                var runs = block.Inlines.OfType<Run>().ToList();
                if (runs.Count > 1 || block.Inlines.Count > 1)
                {
                    foreach (Run run in runs) TranslateRun(run);
                }
                else
                {
                    Original o = Store.GetOrCreateValue(block);
                    string current = block.Text;
                    if (o.Text is null || current != o.TextShown) o.Text = current;
                    o.TextShown = T(o.Text);
                    if (o.TextShown != current) block.Text = o.TextShown;
                }
            }

            if (node is FrameworkElement element)
            {
                if (element.ToolTip is string tip)
                {
                    Original o = Store.GetOrCreateValue(element);
                    if (o.Tip is null || tip != o.TipShown) o.Tip = tip;
                    o.TipShown = T(o.Tip);
                    if (o.TipShown != tip) element.ToolTip = o.TipShown;
                }
                string name = AutomationProperties.GetName(element);
                if (!string.IsNullOrEmpty(name))
                {
                    Original o = Store.GetOrCreateValue(element);
                    if (o.Name is null || name != o.NameShown) o.Name = name;
                    o.NameShown = T(o.Name);
                    if (o.NameShown != name) AutomationProperties.SetName(element, o.NameShown);
                }
            }

            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private static void TranslateRun(Run run)
    {
        if (BindingOperations.IsDataBound(run, Run.TextProperty)) return;
        Original o = Store.GetOrCreateValue(run);
        string current = run.Text;
        if (o.Text is null || current != o.TextShown) o.Text = current;
        o.TextShown = T(o.Text);
        if (o.TextShown != current) run.Text = o.TextShown;
    }
}
