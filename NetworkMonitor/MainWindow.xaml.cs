using LiveCharts;
using LiveCharts.Wpf;
using Microsoft.Extensions.Configuration;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetworkMonitor
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        private DispatcherTimer _timer;
        private bool _isChecking = false; // 実行中フラグ
        private DateTime _nextCheckTime; // 次に確認ダイアログを出す時刻
        private const int CHECK_INTERVAL_SECONDS = 6 * 3600; // 何秒ごとに確認するか

        private readonly string _url;

        public ChartValues<double> ChartValues { get; set; }

        public MainWindow()
        {
            InitializeComponent();

            // 設定ファイルをビルド
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory()) // 実行ファイルの場所
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
            string url = config["ExternalService:Url"];
            Debug.WriteLine($"デバッグ用のURL確認: {url}");

            // ブランクなら異常終了、そうでなければクラス変数に格納
            if (string.IsNullOrWhiteSpace(url))
            {
                string errorMsg = "致命的なエラー: appsettings.json でURLが設定されていません。";
                Debug.WriteLine(errorMsg);
                System.Windows.MessageBox.Show(errorMsg, "設定エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                Environment.Exit(1);
            }
            _url = url;

            // 初回起動時、現在時刻に指定秒数を足して「目標時刻」を設定
            ResetNextCheckTime();

            ChartValues = new ChartValues<double>();

            // n秒ごとに実行するタイマーの設定
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += async (s, e) => await CheckConnection();
            _timer.Start();

            DataContext = this;
        }

        private void ResetNextCheckTime()
        {
            // 現在時刻に設定秒数を加算
            _nextCheckTime = DateTime.Now.AddSeconds(CHECK_INTERVAL_SECONDS);
        }

        private async System.Threading.Tasks.Task CheckConnection()
        {
            // すでにリクエスト実行中なら、今回のタイマー処理は何もせず終了
            if (_isChecking) return;

            // 現在時刻が目標時刻を過ぎているか判定
            if (DateTime.Now >= _nextCheckTime)
            {
                _timer.Stop(); // 一旦タイマーを止める

                var result = MessageBox.Show(
                    $"{CHECK_INTERVAL_SECONDS / 60}分経過しました。監視を継続しますか？",
                    "継続確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    ResetNextCheckTime(); // 次の目標時刻を再設定
                    _timer.Start();    // タイマー再開
                                       // 継続時は、そのまま下の通信処理へ進む
                }
                else
                {
                    Application.Current.Shutdown(); // アプリ終了
                    return;
                }
            }

            _isChecking = true; // 「実行中」にする
            var sw = Stopwatch.StartNew();
            try
            {
                // リクエストを送信
                var response = await _httpClient.GetAsync(_url);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    UpdateStatus(true, sw.ElapsedMilliseconds);
                }
            }
            catch (Exception)
            {
                // タイムアウトや接続無効時にここに来る
                UpdateStatus(false, 1000); // タイムアウトとして1000msで描画
            }
            finally
            {
                _isChecking = false; // 成功・失敗に関わらず、終わったら「未実行」に戻す
            }
        }

        private void UpdateStatus(bool isConnected, long responseTime)
        {
            ChartValues.Add(responseTime);

            // グラフ表示は直近50〜60件程度に制限（見やすさのため）
            if (ChartValues.Count > 60) ChartValues.RemoveAt(0);

            // --- 直近1分間(最大60件)の平均を計算 ---
            double average = ChartValues.Average();
            AverageText.Text = $"直近1分の平均: {average:F1}ms"; // 小数点1位まで表示

            if (isConnected)
            {
                StatusBorder.Background = Brushes.Green;
                StatusText.Text = $"現在: {responseTime}ms";
            }
            else
            {
                StatusBorder.Background = Brushes.Red;
                StatusText.Text = "接続エラー";
            }
        }
    }
}