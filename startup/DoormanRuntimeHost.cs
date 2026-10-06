using System;
using System.Reflection;

internal static class DoormanRuntimeHost
{
    internal static void Apply(object host)
    {
        if(host==null)throw new ArgumentNullException(nameof(host));
        var flags=host.GetType().GetProperty("hideFlags",BindingFlags.Public|BindingFlags.Instance)??throw new MissingMemberException("Runtime host flags are unavailable.");
        int current=Convert.ToInt32(flags.GetValue(host,null));
        int protection=Convert.ToInt32(Enum.Parse(flags.PropertyType,"HideAndDontSave"));
        var persist=flags.DeclaringType!.GetMethod("DontDestroyOnLoad",BindingFlags.Public|BindingFlags.Static)??throw new MissingMethodException("Runtime host persistence is unavailable.");
        if((current&protection)!=protection)flags.SetValue(host,Enum.ToObject(flags.PropertyType,current|protection),null);
        persist.Invoke(null,new[]{host});
    }
}
