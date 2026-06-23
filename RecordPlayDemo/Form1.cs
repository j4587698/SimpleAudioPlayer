using SimpleAudioPlayer;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;

namespace RecordPlayDemo;

public partial class Form1 : Form
{
    private static readonly string RecordingsDir = Path.Combine(AppContext.BaseDirectory, "Recordings");

    private AudioRecorder? _recorder;
    private AudioPlayer? _player;
    private string? _recordedFilePath;
    private System.Windows.Forms.Timer? _timer;
    private DateTime _recordStart;
    private readonly List<string> _sessionFiles = [];

    public Form1()
    {
        InitializeComponent();
        UpdateButtonStates();
    }

    private void btnRecord_Click(object sender, EventArgs e)
    {
        try
        {
            _recorder?.Dispose();
            _recorder = new AudioRecorder();

            Directory.CreateDirectory(RecordingsDir);
            var fileName = $"record_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
            var filePath = Path.Combine(RecordingsDir, fileName);
            if (!_recorder.Start(filePath, RecordingFileFormat.Wav))
            {
                MessageBox.Show($"录音启动失败: {_recorder.LastResult}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _recorder.Dispose();
                _recorder = null;
                return;
            }

            _recordedFilePath = filePath;
            _sessionFiles.Add(filePath);
            _recordStart = DateTime.Now;
            lblStatus.Text = "录音中...";
            StartTimer();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void btnStop_Click(object sender, EventArgs e)
    {
        try
        {
            if (_recorder?.IsRecording == true)
            {
                _recorder.Stop();
                StopTimer();
                lblStatus.Text = $"录音完成: {Path.GetFileName(_recordedFilePath)}";
                _recorder.Dispose();
                _recorder = null;
            }

            if (_player?.PlaybackState == PlaybackState.Playing)
            {
                _player.Stop();
                StopTimer();
                lblStatus.Text = "已停止";
            }

            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void btnPlay_Click(object sender, EventArgs e)
    {
        if (_recordedFilePath == null || !File.Exists(_recordedFilePath))
        {
            MessageBox.Show("没有可播放的录音文件，请先录音。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            _player?.Dispose();
            _player = new AudioPlayer();
            _player.PlayCompleted = () =>
            {
                BeginInvoke(() =>
                {
                    StopTimer();
                    lblStatus.Text = "播放完成";
                    UpdateButtonStates();
                });
            };

            var handler = new FileStreamHandler(_recordedFilePath);
            _player.Load(handler);
            _player.Play();

            lblStatus.Text = "播放中...";
            StartTimer();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartTimer()
    {
        _timer?.Stop();
        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _recordStart;
            lblTime.Text = elapsed.ToString(@"mm\:ss");
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private void UpdateButtonStates()
    {
        var isRecording = _recorder?.IsRecording == true;
        var isPlaying = _player?.PlaybackState == PlaybackState.Playing;

        btnRecord.Enabled = !isRecording && !isPlaying;
        btnPlay.Enabled = !isRecording && !isPlaying && _recordedFilePath != null;
        btnStop.Enabled = isRecording || isPlaying;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer?.Dispose();
        _recorder?.Dispose();
        _player?.Dispose();

        foreach (var file in _sessionFiles)
        {
            try { File.Delete(file); } catch { }
        }

        base.OnFormClosed(e);
    }
}
