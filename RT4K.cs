﻿namespace rt4k_pi;

public class RT4K
{
    public PowerState Power { get; private set; } = PowerState.Unknown;
    private readonly Serial serial;

    private TaskCompletionSource readHappened = new();
    private TaskCompletionSource powerHappened = new();

    public RT4K(Serial serial)
    {
        this.serial = serial;
        serial.RegisterReader(HandleRead);

        // Power state is unknown on start, try to turn on and see what happens
        // TODO: Replace this with an actual query when implemented
        Task.Run(() =>
        {
            Task.Delay(1000).Wait();

            Console.WriteLine("Determining RT4K power state");
            readHappened = new();
            powerHappened = new();

            PowerOn();
            if (!readHappened.Task.Wait(500))
            {
                // Nothing happened, so we must already be on
                Console.WriteLine("RT4K seems to be on");
                Power = PowerState.On;
            }
            else
            {
                // The RT4K turned on, so it was off before. Turn it off again.
                Console.WriteLine("RT4K was off, turning it off again");
                powerHappened.Task.Wait(10000);
                Task.Delay(2000).Wait();
                PowerOff();
            }
        });
    }

    private void HandleRead(string data)
    {
        // Let anything waiting on a read know that it happened
        readHappened.TrySetResult();

        // Update known power state based on observed serial output
        if (data.StartsWith("[MCU] Boot Sequence Complete..."))
        {
            Console.WriteLine("Detected RT4K startup");
            Power = PowerState.On;
            powerHappened.TrySetResult();
        }
        else if (data.StartsWith("[MCU] Entering Sleep Mode"))
        {
            Console.WriteLine("Detected RT4K shutdown");
            Power = PowerState.Off;
            powerHappened.TrySetResult();
        }
    }

    public enum PowerState
    {
        On,
        Off,
        Unknown
    }

    public enum Remote
    {
        Power,
        Menu,
        Up,
        Down,
        Left,
        Right,
        OK,
        Back,
        Diagnostic,
        Status,
        Input,
        Output,
        Scaler,
        SFX,
        ADC,
        Profile,
        Profile1,
        Profile2,
        Profile3,
        Profile4,
        Profile5,
        Profile6,
        Profile7,
        Profile8,
        Profile9,
        Profile10,
        Profile11,
        Profile12,
        Gain,
        Phase,
        Pause,
        Safe,
        Genlock,
        Buffer,
        Resolution4K,
        Resolution1080p,
        Resolution1440p,
        Resolution480p,
        ResolutionUser1,
        ResolutionUser2,
        ResolutionUser3,
        ResolutionUser4,
        Aux1,
        Aux2,
        Aux3,
        Aux4,
        Aux5,
        Aux6,
        Aux7,
        Aux8,
    }

    private static readonly Dictionary<Remote, string> remoteLookup = new()
    {
        { Remote.Power, "pwr"},
        { Remote.Menu, "menu"},
        { Remote.Up, "up"},
        { Remote.Down, "down"},
        { Remote.Left, "left"},
        { Remote.Right, "right"},
        { Remote.OK, "ok"},
        { Remote.Back, "back"},
        { Remote.Diagnostic, "diag"},
        { Remote.Status, "stat"},
        { Remote.Input, "input"},
        { Remote.Output, "output"},
        { Remote.Scaler, "scaler"},
        { Remote.SFX, "sfx"},
        { Remote.ADC, "adc"},
        { Remote.Profile, "prof"},
        { Remote.Profile1, "prof1"},
        { Remote.Profile2, "prof2"},
        { Remote.Profile3, "prof3"},
        { Remote.Profile4, "prof4"},
        { Remote.Profile5, "prof5"},
        { Remote.Profile6, "prof6"},
        { Remote.Profile7, "prof7"},
        { Remote.Profile8, "prof8"},
        { Remote.Profile9, "prof9"},
        { Remote.Profile10, "prof10"},
        { Remote.Profile11, "prof11"},
        { Remote.Profile12, "prof12"},
        { Remote.Gain, "gain"},
        { Remote.Phase, "phase"},
        { Remote.Pause, "pause"},
        { Remote.Safe, "safe"},
        { Remote.Genlock, "genlock"},
        { Remote.Buffer, "buffer"},
        { Remote.Resolution4K, "res4k"},
        { Remote.Resolution1080p, "res1080p"},
        { Remote.Resolution1440p, "res1440p"},
        { Remote.Resolution480p, "res480p"},
        { Remote.ResolutionUser1, "res1"},
        { Remote.ResolutionUser2, "res2"},
        { Remote.ResolutionUser3, "res3"},
        { Remote.ResolutionUser4, "res4"},
        { Remote.Aux1, "aux1"},
        { Remote.Aux2, "aux2"},
        { Remote.Aux3, "aux3"},
        { Remote.Aux4, "aux4"},
        { Remote.Aux5, "aux5"},
        { Remote.Aux6, "aux6"},
        { Remote.Aux7, "aux7"},
        { Remote.Aux8, "aux8"}
    };

    public void SendRemoteString(string remoteString)
    {
        if (Enum.TryParse(remoteString, true, out Remote remote))
        {
            // TODO: Replace power state detection with query when available
            if (remote == Remote.Power)
            {
                PowerToggle();
                return;
            }

            SendRemote(remote);
        }
        else
        {
            Console.WriteLine($"Unrecognized remote string: {remoteString}");
        }
    }

    public void SendRemote(Remote remote)
    {
        serial.WriteLine("remote " + remoteLookup[remote]);
    }

    public void PowerOn()
    {
        serial.WriteLine("pwr on");
    }

    public void PowerOff()
    {
        SendRemote(Remote.Power);
    }

    public void PowerToggle()
    {
        // If we try to toggle power and don't hear from the RT4K for half a second, we know it's not in the expected power state.
        // In that case, we try to toggle the other direction to get back in sync.
        readHappened = new();
        switch (Power)
        {
            case PowerState.Unknown:
            case PowerState.Off:
                PowerOn();
                if (!readHappened.Task.Wait(500)) PowerOff();
                break;
            case PowerState.On:
                PowerOff();
                if (!readHappened.Task.Wait(500)) PowerOn();
                break;
        }
    }
}
