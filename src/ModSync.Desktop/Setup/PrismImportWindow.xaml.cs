using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ModSync.Core.InstanceSetup;
using ModSync.Platform.FileSystem;
using ModSync.Platform.Prism;

namespace ModSync.Desktop.Setup;

public partial class PrismImportWindow : Window
{
    private InstanceImportCoordinator? coordinator;
    private PrismInstallationFacts? installation;
    private CancellationTokenSource? waiting;
    private bool busy;
    private string EvidencePath => Path.Combine(JsonImportRecordStore.DefaultRoot, "prism-import", installation!.DataRootKey, "measurement.json");
    public PrismImportWindow()
    {
        InitializeComponent();
        Closed += (_, _) => waiting?.Cancel();
    }
    private async Task Initialize()
    {
        var current = await PrismInstallation.ValidateAsync(Executable.Text, DataRoot.Text);
        if (installation != current)
        {
            installation = current;
            coordinator = new(new PrismImportGateway(current), new JsonImportRecordStore(JsonImportRecordStore.DefaultRoot, prismDataRoot: current.DataRoot));
        }
    }
    private async Task<bool> Capture(string stage)
    {
        await Initialize();
        var scope = ProtectedNames.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!File.Exists(EvidencePath) && stage != "beforePrismLaunch" && !(stage == "beforeImport" && AlreadyRunning.IsChecked == true))
            throw new IOException("最初は起動前、または起動中の取り込み前の基準を採取してください。");
        var snapshot = await new InstanceTreeSnapshotter().CaptureAsync(installation!.InstancesRoot, scope, stage);
        var result = await InstanceTreeSnapshotter.AppendEvidenceAsync(EvidencePath, snapshot,
            AlreadyRunning.IsChecked == true ? "prismAlreadyRunning" : "beforePrismLaunch", AlreadyRunning.IsChecked == true);
        Add($"保護証拠: {EvidencePath} / {result.OverallResult}");
        if (Guid.TryParse(OperationId.Text, out var id)) await InstanceTreeSnapshotter.AttachOperationAsync(EvidencePath, id, installation.FileVersion);
        if (result.OverallResult == "changed")
        {
            RegisterButton.IsEnabled = false;
            throw new IOException("既存インスタンスに変更を検出しました。以後の取り込み・登録を停止し、証拠を保全してハブへ報告してください。");
        }
        return result.Snapshots.Count > 1;
    }
    private async Task EnsureBaseline()
    {
        await Initialize();
        if (!File.Exists(EvidencePath)) throw new IOException("起動前、または起動中の現在状態を基準として先に採取してください。");
        await using (var evidence = PrismInstallation.SharedRead(EvidencePath))
        {
            var document = await JsonSerializer.DeserializeAsync<PrismImportMeasurement>(evidence, ImportJson.Options) ?? throw new IOException("保護証拠が空です。");
            var protection = document.ProtectedComparison;
            if (protection.BaselineKind == "beforePrismLaunch" && !protection.Snapshots.Any(s => s.Stage == "afterPrismLaunch"))
                throw new IOException("本人がPrismを起動した後のafterPrismLaunchを採取・比較してから取り込みへ進めてください。");
            if (protection.BaselineKind is not "beforePrismLaunch" and not "prismAlreadyRunning") throw new IOException("基準を確認できません。");
        }
        await Capture("beforeImport");
    }
    private async void CaptureClicked(object sender, RoutedEventArgs e) => await Run(async () =>
        { await Capture(((ComboBoxItem)SnapshotStage.SelectedItem).Content.ToString()!); });
    private async void StartClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        RegisterButton.IsEnabled = false;
        await EnsureBaseline();
        var input = await ImportInputLoader.LoadAsync(InputDefinition.Text, FailureProbe.IsChecked == true);
        waiting = new CancellationTokenSource();
        var result = await coordinator!.StartAsync(input, new Progress<ImportAttemptStatus>(Show), waiting.Token);
        Show(result);
        if (result.State == ImportAttemptState.Verified) await Capture("afterImport");
        await InstanceTreeSnapshotter.AttachOperationAsync(EvidencePath, result.OperationId, installation!.FileVersion);
        Add("処理記録: " + Path.Combine(JsonImportRecordStore.DefaultRoot, "prism-import", installation!.DataRootKey, "operations", result.OperationId.ToString(), "operation.json"));
    });
    private void CancelClicked(object sender, RoutedEventArgs e) => waiting?.Cancel();
    private async void RegisterClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await Capture("afterImport");
        Show(await coordinator!.RegisterAsync(Id()));
    });
    private async void RecheckClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await Initialize(); waiting = new CancellationTokenSource();
        Show(await coordinator!.RecheckAsync(Id(), waiting.Token));
    });
    private async void ReadinessClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await Initialize();
        Show(await coordinator!.CheckReadinessAsync(Id()));
        await Capture("afterReadinessCheck");
    });
    private async void ReportClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await Initialize();
        await coordinator!.ReportUserActionAsync(Id(), ((ComboBoxItem)UserReport.SelectedItem).Content.ToString()!);
        Add("本人の申告を記録しました。成功判定は変更しません。");
    });
    private Guid Id() => Guid.Parse(OperationId.Text);
    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; StartButton.IsEnabled = false;
        foreach (var control in new Control[] { Executable, DataRoot, InputDefinition, ProtectedNames, AlreadyRunning, FailureProbe }) control.IsEnabled = false;
        try { await action(); }
        catch (Exception error) { Status.Text = "処理を進められません: " + error.Message; Add(Status.Text); }
        finally
        {
            busy = false; StartButton.IsEnabled = true;
            foreach (var control in new Control[] { Executable, DataRoot, InputDefinition, ProtectedNames, AlreadyRunning, FailureProbe }) control.IsEnabled = true;
        }
    }
    private void Add(string text) { Log.AppendText(DateTimeOffset.Now.ToString("HH:mm:ss") + " " + text + "\n"); Log.ScrollToEnd(); }
    private void Show(ImportAttemptStatus status)
    {
        OperationId.Text = status.OperationId.ToString();
        Status.Text = StateLabel(status.State) + " / " + status.Reason;
        if (status.Readiness != null)
        {
            Status.Text += "\n準備状況: " + ReadinessLabel(status.Readiness.Level);
            foreach (var item in status.Readiness.Missing) Add($"準備の不足・未確認: {item.Kind} / {item.Path} / {item.Reason}");
        }
        RegisterButton.IsEnabled = status.State == ImportAttemptState.Verified;
        Add(Status.Text);
    }
    private static string ReadinessLabel(ReadinessLevel level) => level switch
    {
        ReadinessLevel.Declared => "バージョン宣言のみ一致（準備は未完了）",
        ReadinessLevel.MetadataResolved => "メタデータまで一致（本体・ライブラリーの準備は未完了）",
        ReadinessLevel.FilesPresent => "本体・ライブラリーは一致（ローダー処理の準備は未完了）",
        ReadinessLevel.Ready => "本体・ライブラリー・ローダー生成物の準備を確認しました（ゲーム起動・参加は未確認）",
        ReadinessLevel.Mismatch => "期待と不一致（準備できていません）",
        ReadinessLevel.Unconfirmed => "確認できない項目があります（準備完了として扱いません）",
        _ => "未確認"
    };
    public static string StateLabel(ImportAttemptState state) => state switch
    {
        ImportAttemptState.Prepared => "取り込み用ファイルを準備しました",
        ImportAttemptState.ImportRequested => "Prismへ取り込みを依頼しました（まだ完了していません）",
        ImportAttemptState.HandedOffToRunningPrism => "起動中のPrismへ渡しました。Prismの画面を確認してください",
        ImportAttemptState.AwaitingUserConfirmation => "Prismの取り込み確認を待っています",
        ImportAttemptState.Writing => "Prismがインスタンスを作成中です",
        ImportAttemptState.Verified => "配置を照合しました。登録はまだ確定していません",
        ImportAttemptState.Committing => "登録を保存しています",
        ImportAttemptState.Registered => "専用インスタンスとして登録しました",
        ImportAttemptState.AlreadyRegistered => "このインスタンスは登録済みです",
        ImportAttemptState.TimedOut => "待機時間内に取り込みを確認できませんでした（成功していません）",
        ImportAttemptState.StagingVanished => "Prismの作業が途中で終わりました。キャンセルまたは失敗の可能性があります（登録していません）",
        ImportAttemptState.Mismatch => "新しいインスタンスの内容が期待と一致しません（登録していません）",
        ImportAttemptState.Ambiguous => "対象を一つに特定できません（登録していません）",
        ImportAttemptState.PrerequisiteFailed => "取り込みを開始できません",
        ImportAttemptState.Unobservable => "識別情報またはフォルダーを確認できません（登録していません）",
        ImportAttemptState.CommitFailed => "登録を保存できませんでした。登録は確定していません",
        ImportAttemptState.CancelledByApp => "待機を中止しました。Prism側の取り込みは続いている可能性があります",
        _ => "状態を確認できません"
    };
}
