// See https://aka.ms/new-console-template for more information

using SimpleAudioPlayer;
using SimpleAudioPlayer.Handles;

var player = new AudioPlayer();

var handle = new FileStreamHandler("./test.mp3");
player.Load(handle);

var duration = player.GetDuration();
Console.WriteLine("duration: " + duration);
player.DeviceNotificationChanged = type => Console.WriteLine($"状态改变: {type}");
player.Play();
while (true)
{
    var keyInfo = Console.ReadKey();
    switch (keyInfo.Key)
    {
        case ConsoleKey.Q:
            player.Stop();
            break;
        case ConsoleKey.S:
            player.Pause();
            break;
        case ConsoleKey.P:
            player.Play();
            break;
        case ConsoleKey.R:
        {
            var time = player.GetTime();
            if (duration - time < 2)
            {
                player.Seek(0);
            }
            player.Seek(time + 2);
            break;
        }
        case ConsoleKey.T:
            Console.WriteLine(player.GetTime());
            break;
        case ConsoleKey.UpArrow:
            player.Volume += 0.1f;
            Console.WriteLine(player.Volume);
            break;
        case ConsoleKey.DownArrow:
            player.Volume -= 0.1f;
            Console.WriteLine(player.Volume);
            break;
        case ConsoleKey.D:
            Console.WriteLine(player.GetPlayState());
            break;
    }
}

