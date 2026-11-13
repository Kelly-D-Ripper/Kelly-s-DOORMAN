namespace KellysJOINCHECK
{
    internal enum RestartLaunchAction { Wait, Launch, Detected, Cancelled, Timeout }

    // Only the external helper polls this policy. Unknown Steam state is not an idle signal.
    internal sealed class RestartLaunchPolicy
    {
        internal const double CooldownSeconds=10, IdleSeconds=2, RetrySeconds=30, UnknownRetrySeconds=120, TimeoutSeconds=240;
        internal const int MaxAttempts=2;
        private double? idleSince,lastLaunch;
        internal int Attempts { get; private set; }

        internal RestartLaunchAction Next(double elapsed,bool? steamBusy,bool gameFound,bool cancelled)
        {
            if(cancelled)return RestartLaunchAction.Cancelled;
            if(gameFound)return RestartLaunchAction.Detected;
            if(elapsed>=TimeoutSeconds)return RestartLaunchAction.Timeout;
            if(steamBusy==false) { if(!idleSince.HasValue)idleSince=elapsed; }
            else idleSince=null;
            if(elapsed<CooldownSeconds||steamBusy==true||(steamBusy==false&&elapsed-idleSince!.Value<IdleSeconds))return RestartLaunchAction.Wait;
            double retry=steamBusy.HasValue?RetrySeconds:UnknownRetrySeconds;
            if(Attempts>=MaxAttempts||(lastLaunch.HasValue&&elapsed-lastLaunch.Value<retry))return RestartLaunchAction.Wait;
            return RestartLaunchAction.Launch;
        }
        internal void Launched(double elapsed)
        {
            if(Attempts>=MaxAttempts)throw new System.InvalidOperationException("Restart launch limit reached.");
            Attempts++;lastLaunch=elapsed;
        }
    }
}
