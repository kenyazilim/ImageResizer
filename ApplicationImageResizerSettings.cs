using ImageResizer.Configuration;
using ImageResizer.Plugins.Basic;
using ImageResizer.Plugins.HybridCache;
using ImageResizer.Plugins.Imageflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Text;
using System.Web.Hosting;
using ImageResizer;
using ImageResizer.Plugins.Licensing;

/// <summary>
/// Hangi özelliklerin dinamik olarak etkinleştirileceğini belirleyen ayarları tutar.
/// </summary>
public class FeatureSettings
{
    /// <summary>
    /// Watermark (filigran) özelliğini etkinleştirir. Bu, yapılandırmaya <watermarks> bölümünü ekler.
    /// </summary>
    public bool EnableWatermark { get; set; } = true; // Varsayılan olarak açık

    /// <summary>
    /// HybridCache (disk ve bellek önbellekleme) eklentisini etkinleştirir.
    /// </summary>
    public bool EnableHybridCache { get; set; } = true; // Varsayılan olarak açık
}


/// <summary>
/// ImageResizer v5 için dinamik ve kod tabanlı yapılandırma sağlar.
/// Bu sınıf, ImageResizer kütüphanesinin tüm ayarlarını ve plugin yüklemelerini
/// web.config dosyasına bağımlı kalmadan, C# kodu üzerinden yönetmek için tasarlanmıştır.
/// </summary>
public static class ApplicationImageResizerSettings
{
    private static bool _isConfigured = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// ImageResizer kütüphanesini başlatır ve yapılandırır.
    /// Bu metot, Global.asax.cs dosyasındaki Application_Start olayında yalnızca bir kez çağrılmalıdır.
    /// </summary>
    public static void Configure()
    {
        if (_isConfigured) return;
        lock (_lock)
        {
            if (_isConfigured) return;

            // Dinamik ayarları buradan yükleyin (örneğin veritabanından veya appsettings.json'dan)
            var featureSettings = new FeatureSettings
            {
                EnableWatermark = true,
                EnableHybridCache = true
            };

            // --- Temel Yapılandırma Ayarları ---
            long maxTotalMegapixels = 45;
            int maxImageWidth = 8000;
            int maxImageHeight = 8000;
            string diagnosticsMode = "localhost";
            string licenseKey = "R5_..."; // Gerçek lisans anahtarınızla değiştirin.

            // defaultPipelineCommands: URL'de özel bir ayar belirtilmediği sürece, tüm görsellere uygulanacak varsayılan komut setidir.
            // Bu, sitenizdeki tüm görseller için tutarlı bir kalite ve optimizasyon standardı sağlar.
            //
            // Önerilen Ayarların Açıklaması:
            // quality=75: Hem JPEG hem de WebP formatları için varsayılan kalite seviyesidir. 75, görsel kalite ve dosya boyutu arasında iyi bir denge sunar.
            // format=webp: Tarayıcı destekliyorsa, görüntüyü otomatik olarak WebP formatına dönüştürür. WebP, genellikle aynı kalitede daha küçük dosyalar sunar.
            //              Tarayıcı WebP desteklemiyorsa, ImageResizer otomatik olarak JPEG gibi varsayılan bir formata döner.
            // autorotate=true: Görselin EXIF meta verisindeki yönlendirme bilgisine göre otomatik olarak döndürülmesini sağlar. Özellikle mobil cihazlardan gelen fotoğraflar için önemlidir.
            // subsampling=420: JPEG sıkıştırmasında renk alt örneklemesini belirler. 4:2:0, iyi sıkıştırma ve kabul edilebilir kalite dengesi sunar.
            // strip=all: Görselden tüm meta verileri (EXIF, IPTC vb.) kaldırır. Bu, gizliliği artırır ve dosya boyutunu önemli ölçüde küçültür.
            // jpeg.progressive=true: JPEG görsellerini aşamalı (progressive) olarak kaydeder. Bu, yavaş bağlantılarda kullanıcı deneyimini iyileştirir.
            string defaultPipelineCommands = "quality=30&amp;format=webp&amp;autorotate=false&amp;subsampling=420&amp;strip=all&amp;jpeg.progressive=true";

            double clientCacheMinutes = 525600; // 1 yıl

            // --- Önbellek Yolu ---
            string appDataPath = HostingEnvironment.MapPath("~/App_Data");
            string safeCachePath = Path.Combine(appDataPath, "cache");
            if (!Directory.Exists(safeCachePath))
            {
                Directory.CreateDirectory(safeCachePath);
            }
            int hybridCacheCacheSizeMb = 2048;
            int hybridWriteQueueMemoryMb = 100;

            // =================================================================================================
            // BÖLÜM 1: XML YAPILANDIRMASINI OLUŞTURMA (V5 UYUMLU)
            // =================================================================================================
            var resizerXmlConfig = new StringBuilder();
            resizerXmlConfig.AppendLine("<resizer>");
            resizerXmlConfig.AppendLine($"<sizelimits totalMegapixels=\"{maxTotalMegapixels}\" width=\"{maxImageWidth}\" height=\"{maxImageHeight}\" />");
            resizerXmlConfig.AppendLine($"<pipeline defaultCommands=\"{defaultPipelineCommands}\" />");
            resizerXmlConfig.AppendLine($"<diagnostics enableFor=\"{diagnosticsMode}\" />");
            resizerXmlConfig.AppendLine($"<clientcache minutes=\"{clientCacheMinutes}\" />");

            // Watermark yapılandırmasını dinamik olarak ekle
            if (featureSettings.EnableWatermark)
            {
                // Imageflow'un beklediği formatta watermark yapılandırması.
                // Bu yapılandırma, URL'de &watermark=sample kullanıldığında
                // ~/watermarks/sample.png dosyasını sağ alt köşeye yerleştirir.
                resizerXmlConfig.AppendLine("<watermarks>");
                resizerXmlConfig.AppendLine("<group name=\"sample\">");
                resizerXmlConfig.AppendLine("<image path=\"~/watermarks/sample.png\" imageQuery=\"opacity=0.8\" align=\"bottomright\"/>");
                resizerXmlConfig.AppendLine("</group>");
                resizerXmlConfig.AppendLine("</watermarks>");
            }

            resizerXmlConfig.AppendLine("</resizer>");

            // =================================================================================================
            // BÖLÜM 2: YAPILANDIRMAYI VE PLUGIN'LERİ YÜKLEME
            // =================================================================================================
            var resizerSection = new ResizerSection(resizerXmlConfig.ToString());
            var c = new Config(resizerSection);

            ILogger logger = NullLoggerFactory.Instance.CreateLogger("ImageResizer");

            // --- Gerekli Eklentileri Programlı Olarak Yükle ---
            // Not: Config(resizerSection) kurucusu, XML'de belirtilen temel eklentileri
            // (SizeLimiting, ClientCache, Diagnostics) zaten yükler.

            new DefaultEncoder().Install(c);
            new Presets().Install(c);
            new DefaultSettings().Install(c);
            new WebConfigLicenseReader().Install(c); // web.config'den lisans okumayı sağlar (opsiyonel).

            // Imageflow, Watermark dahil olmak üzere ana görüntü işleme motorudur.
            new ImageflowBackendPlugin().Install(c);

            // Lisans anahtarını koddan yükle
            if (!string.IsNullOrEmpty(licenseKey) && licenseKey.StartsWith("R5_"))
            {
                new StaticLicenseProvider(licenseKey).Install(c);
            }

            // HybridCache eklentisini dinamik olarak yükle
            if (featureSettings.EnableHybridCache)
            {
                var cacheOptions = new HybridCacheOptions(safeCachePath)
                {
                    CacheSizeMb = hybridCacheCacheSizeMb,
                    WriteQueueMemoryMb = hybridWriteQueueMemoryMb
                };
                new HybridCachePlugin(cacheOptions, logger).Install(c);
            }

            _isConfigured = true;
        }
    }
}
