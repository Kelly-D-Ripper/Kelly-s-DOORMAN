using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace KellysJOINCHECK
{
    internal static class BrowserQueryPatch
    {
        internal static IEnumerable<CodeInstruction> Inject(IEnumerable<CodeInstruction> instructions,MethodInfo query,MethodInfo rewrite)
        {
            var code = instructions.Select(i=>new CodeInstruction(i)).ToList();
            var matches = code.Select((instruction,index)=>new { instruction,index }).Where(x=>x.instruction.Calls(query)).ToArray();
            if (matches.Length!=1) throw new InvalidOperationException("Browser query contract changed");
            int position = matches[0].index;
            var original = code[position];
            var injected = new CodeInstruction(OpCodes.Call,rewrite);
            // Branches and exception-block starts execute the rewrite before the stock call.
            // Exception-block ends remain after the stock call.
            injected.labels.AddRange(original.labels); original.labels.Clear();
            foreach (var block in original.blocks.Where(b=>b.blockType!=ExceptionBlockType.EndExceptionBlock).ToArray())
            { injected.blocks.Add(block); original.blocks.Remove(block); }
            code.Insert(position,injected);
            return code;
        }
    }
}
