using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KellysJOINCHECK
{
    // Copy collection contents as well as references. Restoring only a reference does
    // not undo Add calls on Blueprinter's shared dictionaries and hardpoint lists.
    internal sealed class ModStateSnapshot
    {
        private sealed class Slot
        {
            internal Func<object?> Get=null!;
            internal Action<object?> Set=null!;
            internal object? Reference, Contents;
            internal bool Guard, Unordered;
        }
        private readonly List<Slot> slots=new List<Slot>();
        internal void Field(object? owner,Type type,string name,bool guard=true,bool unordered=false)
        {
            var field=type.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static) ?? throw new MissingFieldException(type.FullName,name);
            Value(()=>field.GetValue(owner),v=>field.SetValue(owner,v),guard,unordered);
        }
        internal void Value(Func<object?> get,Action<object?> set,bool guard=true,bool unordered=false)
        {
            var value=get(); slots.Add(new Slot { Get=get, Set=set, Reference=value, Contents=Copy(value), Guard=guard, Unordered=unordered });
        }
        internal ModStateSnapshot CaptureCurrent()
        {
            var result=new ModStateSnapshot(); foreach(var slot in slots) result.Value(slot.Get,slot.Set,slot.Guard,slot.Unordered); return result;
        }
        internal bool Unchanged() => slots.Where(s=>s.Guard).All(s=>Equal(s.Get(),s.Reference,s.Contents,s.Unordered));
        internal void Restore()
        {
            foreach(var slot in slots)
            {
                var reference=slot.Reference;
                if (reference is IDictionary dictionary && slot.Contents is DictionaryEntry[] entries)
                { dictionary.Clear(); foreach(var entry in entries) dictionary.Add(entry.Key,entry.Value); }
                else if (reference is Array array && slot.Contents is object?[] elements)
                { for(int i=0;i<array.Length;i++) array.SetValue(elements[i],i); }
                else if (reference is IList list && slot.Contents is object?[] items)
                { list.Clear(); foreach(var item in items) list.Add(item); }
                if (!ReferenceEquals(slot.Get(),reference)) slot.Set(reference);
            }
        }
        private static object? Copy(object? value)
        {
            if (value is IDictionary dictionary) { var entries=new List<DictionaryEntry>(); foreach(DictionaryEntry entry in dictionary) entries.Add(entry); return entries.ToArray(); }
            if (value is IList list) return list.Cast<object?>().ToArray();
            return value;
        }
        private static bool Equal(object? now,object? reference,object? contents,bool unordered)
        {
            if (reference is IDictionary && now is IDictionary dictionary && contents is DictionaryEntry[] entries)
                return ReferenceEquals(now,reference) && dictionary.Count==entries.Length && entries.All(e=>dictionary.Contains(e.Key)&&Equals(dictionary[e.Key],e.Value));
            if (reference is IList && now is IList list && contents is object?[] items)
                return ReferenceEquals(now,reference) && list.Count==items.Length && (unordered?SameMembers(list.Cast<object?>().ToArray(),items):list.Cast<object?>().SequenceEqual(items));
            return Equals(now,reference);
        }
        private static bool SameMembers(object?[] current,object?[] expected) => current.All(item=>current.Count(x=>Equals(x,item))==expected.Count(x=>Equals(x,item)));
    }
    internal sealed class PrefabHashInput
    {
        internal string Key=""; internal int Sibling=0, Original; internal bool InitiallyVisible;
    }
    internal static class ContentPrefabHashes
    {
        // Matches Blueprinter 2.0.1's sorted allocation, using the original loaded
        // prefab values rather than values left over from the previous profile.
        internal static Dictionary<PrefabHashInput,int> Assign(IEnumerable<int> native,IEnumerable<PrefabHashInput> input)
        {
            var ordered=input.OrderBy(x=>x.Key,StringComparer.Ordinal).ThenBy(x=>x.Sibling).ToList();
            var assigned=new HashSet<int>(native.Where(x=>x!=0));
            foreach(var item in ordered.Where(x=>x.InitiallyVisible && x.Original!=0)) assigned.Add(item.Original);
            var result=new Dictionary<PrefabHashInput,int>(); int next=1;
            foreach(var item in ordered)
            {
                if (item.Original!=0 && assigned.Add(item.Original)) { result.Add(item,item.Original); continue; }
                while(assigned.Contains(next)) next++;
                result.Add(item,next); assigned.Add(next++);
            }
            return result;
        }
    }
}
