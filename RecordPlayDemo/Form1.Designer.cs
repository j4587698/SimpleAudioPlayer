namespace RecordPlayDemo;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;
    private Button btnRecord;
    private Button btnStop;
    private Button btnPlay;
    private Label lblStatus;
    private Label lblTime;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        btnRecord = new Button();
        btnStop = new Button();
        btnPlay = new Button();
        lblStatus = new Label();
        lblTime = new Label();
        SuspendLayout();

        btnRecord.Location = new Point(30, 30);
        btnRecord.Size = new Size(100, 40);
        btnRecord.Text = "录音";
        btnRecord.Click += btnRecord_Click;

        btnStop.Location = new Point(150, 30);
        btnStop.Size = new Size(100, 40);
        btnStop.Text = "停止";
        btnStop.Click += btnStop_Click;

        btnPlay.Location = new Point(270, 30);
        btnPlay.Size = new Size(100, 40);
        btnPlay.Text = "播放";
        btnPlay.Click += btnPlay_Click;

        lblStatus.Location = new Point(30, 90);
        lblStatus.Size = new Size(340, 25);
        lblStatus.Text = "就绪";

        lblTime.Location = new Point(30, 120);
        lblTime.Size = new Size(340, 25);
        lblTime.Text = "00:00";
        lblTime.Font = new Font(Font.FontFamily, 14f);

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(400, 170);
        Controls.Add(btnRecord);
        Controls.Add(btnStop);
        Controls.Add(btnPlay);
        Controls.Add(lblStatus);
        Controls.Add(lblTime);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Text = "录音回放示例";
        StartPosition = FormStartPosition.CenterScreen;
        ResumeLayout(false);
    }
}
