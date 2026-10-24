using System;
using GameColorGuard.Core;
namespace GameColorGuard.Tweaks;

/// <summary>Ayarın ne zaman uygulanacağı.</summary>
internal enum TweakScope
{
    /// <summary>İzlenen bir oyun çalıştığı sürece; oyun kapanınca geri alınır.</summary>
    GameRunning,
    /// <summary>İzlenen oyun ön plandayken; Alt+Tab'da geri alınır.</summary>
    GameFocused,
    /// <summary>Kullanıcı açar/kapatır; kapatınca önceki değer geri gelir.</summary>
    Persistent
}

/// <summary>Sistemden okunan güncel durum.</summary>
internal readonly record struct TweakState(bool IsApplied, string Summary);

/// <summary>
/// Geri alınabilir, kullanıcı düzeyinde bir sistem ayarı. Apply önceki değeri saklar, Revert onu geri yazar.
/// Bütün çağrılar arayüz iş parçacığından yapılır.
/// </summary>
internal interface ITweak
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    TweakScope Scope { get; }
    /// <summary>Bu makinede uygulanabilir mi (ör. güç modu masaüstü planında desteklenmeyebilir).</summary>
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    /// <summary>Şu anda bizim tarafımızdan uygulanmış (geri alınacak bir önceki değer saklı) mı?</summary>
    bool IsActive { get; }
    /// <summary>Önceki oturumda uygulanmış bulundu: geri alma Windows varsayılanına döner.</summary>
    void MarkActiveWithoutCapture();
    /// <summary>Yakalanan önceki değeri taşınabilir metne çevirir (oturumlar arası saklama); yoksa null.</summary>
    string? ExportPrevious();
    /// <summary>Önceki oturumda yakalanan değeri yükler ve ayarı etkin sayar.</summary>
    void ImportPrevious(string data);

    TweakState Read();
    bool Apply(out string? error);
    bool Revert(out string? error);
}

/// <summary>Ortak kalıp: önceki değeri sakla, hataları günlüğe yaz.</summary>
internal abstract class TweakBase : ITweak
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract TweakScope Scope { get; }
    public virtual bool IsSupported => true;
    public virtual string? UnsupportedReason => null;
    public bool IsActive { get; protected set; }

    /// <summary>Önceki oturumda uygulanmış bulundu: önceki değer bilinmiyor, geri alma Windows varsayılanına döner.</summary>
    public void MarkActiveWithoutCapture() => IsActive = true;

    public virtual string? ExportPrevious() => null;
    public void ImportPrevious(string data)
    {
        try { ImportPreviousCore(data); IsActive = true; }
        catch (Exception ex) { AppLogger.Warn($"{Title}: saklanan önceki değer okunamadı ({ex.Message})"); }
    }
    protected virtual void ImportPreviousCore(string data) { }

    protected static string ToJson<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);
    protected static T? FromJson<T>(string data) => System.Text.Json.JsonSerializer.Deserialize<T>(data);

    public abstract TweakState Read();

    public bool Apply(out string? error)
    {
        error = null;
        if (!IsSupported) { error = UnsupportedReason ?? "desteklenmiyor"; return false; }
        try
        {
            if (!ApplyCore(out error)) { AppLogger.Warn($"{Title}: uygulanamadı — {error}"); return false; }
            IsActive = true;
            AppLogger.Info($"{Title}: uygulandı");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.Write($"{Title}: uygulama hatası", ex);
            return false;
        }
    }

    public bool Revert(out string? error)
    {
        error = null;
        if (!IsActive) return true;
        try
        {
            bool ok = RevertCore(out error);
            IsActive = false;
            if (ok) AppLogger.Info($"{Title}: geri alındı");
            else AppLogger.Warn($"{Title}: geri alınamadı — {error}");
            return ok;
        }
        catch (Exception ex)
        {
            IsActive = false;
            error = ex.Message;
            AppLogger.Write($"{Title}: geri alma hatası", ex);
            return false;
        }
    }

    protected abstract bool ApplyCore(out string? error);
    protected abstract bool RevertCore(out string? error);
}
