// See https://aka.ms/new-console-template for more information

using SimpleAudioPlayer;
using SimpleAudioPlayer.Handles;

var player = new AudioPlayer();
player.ChangeHandler(new FileStreamHandler("D:\\119127515.mp3"));

var duration = player.GetDuration();
Console.WriteLine("duration: " + duration);
player.Play();
while (true)
{
    var keyInfo = Console.ReadKey();
    if (keyInfo.Key == ConsoleKey.Q)
    {
        player.Stop();
        break;
    }
    else if(keyInfo.Key == ConsoleKey.S)
    {
        player.Pause();
    }
    else if(keyInfo.Key == ConsoleKey.P)
    {
        player.Play();
    }
    else if(keyInfo.Key == ConsoleKey.R)
    {
        var time = player.GetTime();
        if (duration - time < 2)
        {
            player.Seek(0);
        }
        player.Seek(time + 2);
    }
    else if (keyInfo.Key == ConsoleKey.T)
    {
        Console.WriteLine(player.GetTime());
    }
}

