using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Cli.Commands;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // İlk Ctrl+C işlemi düzgünce durdurur; ikincisi süreci sonlandırır.
    if (cts.IsCancellationRequested)
        return;
    e.Cancel = true;
    AnsiConsole.MarkupLine("[yellow]Durduruluyor...[/]");
    cts.Cancel();
};

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("wa");
    config.AddCommand<RecordCommand>("record")
        .WithDescription("Tarayıcı açar ve yaptığınız işlemleri kaydeder.")
        .WithExample("record", "https://ornek.com", "-o", "giris.wa.json");
    config.AddCommand<ShowCommand>("show")
        .WithDescription("Senaryonun adımlarını okunabilir biçimde listeler.")
        .WithExample("show", "giris.wa.json");
    config.AddCommand<ColumnsCommand>("columns")
        .WithDescription("Excel/CSV/JSON veri kaynağının kolonlarını ve ilk satırlarını gösterir.")
        .WithExample("columns", "kisiler.xlsx");
    config.AddCommand<BindCommand>("bind")
        .WithDescription("Adımları veri kaynağı kolonlarına bağlar (etkileşimli ya da --step/--column ile).")
        .WithExample("bind", "giris.wa.json", "--data", "kisiler.csv")
        .WithExample("bind", "giris.wa.json", "--step", "3", "--column", "isim");
    config.AddCommand<EditCommand>("edit")
        .WithDescription("Bir adımın değerini, bekleme süresini veya etkinliğini değiştirir.")
        .WithExample("edit", "giris.wa.json", "--step", "4", "--delay", "2000");
    config.AddCommand<AddWaitCommand>("add-wait")
        .WithDescription("Senaryoya bekleme adımı ekler: sabit süre, bir yazı ya da bir öğe görünene kadar.")
        .WithExample("add-wait", "giris.wa.json", "--after", "5", "--text", "Sipariş alındı")
        .WithExample("add-wait", "giris.wa.json", "--element", "7", "--timeout", "60000");
    config.AddCommand<PlayCommand>("play")
        .WithDescription("Senaryoyu N kez ya da veri kaynağındaki her satır için oynatır.")
        .WithExample("play", "giris.wa.json", "-n", "50")
        .WithExample("play", "giris.wa.json", "--data", "kisiler.xlsx", "--headless");
    config.AddCommand<InstallCommand>("install")
        .WithDescription("Playwright tarayıcısını indirir (chromium, firefox, webkit).");
});

return await app.RunAsync(args, cts.Token);
