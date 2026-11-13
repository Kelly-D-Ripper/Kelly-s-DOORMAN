using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class StatusWindow : Form
{
    private readonly Label heading,body;
    private readonly ProgressBar progress;
    private bool finished;
    internal int Result=0;
    protected override bool ShowWithoutActivation=>true;
    internal StatusWindow()
    {
        Text="DOORMAN | Restarting Nuclear Option";StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(520,220);BackColor=Color.FromArgb(41,51,56);ForeColor=Color.White;
        heading=new Label { Text="Configuring mods",Location=new Point(24,24),Size=new Size(472,40),Font=new Font("Segoe UI",18),ForeColor=Color.FromArgb(75,219,131) };
        body=new Label { Text="Your selection is saved. Waiting for the current game to close.",Location=new Point(26,78),Size=new Size(468,75),Font=new Font("Segoe UI",11) };
        progress=new ProgressBar { Location=new Point(26,168),Size=new Size(468,16),Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=35 };
        Controls.AddRange(new Control[]{heading,body,progress});
        FormClosing+=(sender,e)=>{if(!finished){e.Cancel=true;Hide();}};
    }
    internal void Stage(string title,string text,bool error=false)
    {
        if(IsDisposed||!IsHandleCreated)return;
        BeginInvoke(new Action(()=>{if(IsDisposed)return;heading.Text=title;body.Text=text;if(error){Result=1;progress.Visible=false;}}));
    }
    internal void Finish(bool close)
    {finished=true;if(close||!Visible)Close();}
}
