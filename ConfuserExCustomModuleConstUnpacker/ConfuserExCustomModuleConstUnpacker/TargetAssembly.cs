using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using System.IO;

using System.Reflection;
using System.Reflection.Emit;

using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace ConfuserExCustomModuleConstUnpacker
{
    class TargetAssembly
    {
        public static bool Unchecked = false;

        public static TargetAssembly LoadFile(string path)
        {
            return new TargetAssembly(path);
        }

        private string InFile;

        private TargetAssembly(string path)
        {
            InFile = path;
        }

        public static readonly string dummy_stub_dll_name = "cexcmcup_dummy.dll";
        public static readonly string module_out_dll_name = "cexcmcup_module.dll";

        private static readonly string[] module_call_whitelist =
        {
            "System.Int32 System.IO.Stream::ReadByte()",
            "System.Void System.Object::.ctor()",
            "System.UInt32 System.Math::Max(System.UInt32,System.UInt32)",
            "System.Void System.Buffer::BlockCopy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)",
            "System.Void System.IO.Stream::Write(System.Byte[],System.Int32,System.Int32)",
            "System.Void System.IO.MemoryStream::.ctor(System.Byte[])",
            "System.Int32 System.IO.Stream::Read(System.Byte[],System.Int32,System.Int32)",
            "System.Void System.IO.MemoryStream::.ctor(System.Byte[],System.Boolean)",
            "System.Int64 System.IO.Stream::get_Length()",
            "System.Void System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(System.Array,System.RuntimeFieldHandle)",
            "System.Text.Encoding System.Text.Encoding::get_UTF8()",
            "System.String System.Text.Encoding::GetString(System.Byte[],System.Int32,System.Int32)",
            "System.String System.String::Intern(System.String)",
            "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)",
            "System.Type System.Type::GetElementType()",
            "System.Array System.Array::CreateInstance(System.Type,System.Int32)",
        };

        void CreateDummy()
        {
            // this can be a one time thing. it could also be done in dnlib but i was too dumb to find ModuleDefUser
            var ABuilder = Thread.GetDomain().DefineDynamicAssembly(new AssemblyName("Dummy"), AssemblyBuilderAccess.RunAndSave);
            var MBuilder = ABuilder.DefineDynamicModule("DummyMod", dummy_stub_dll_name);
            var tb = MBuilder.DefineType("DummyType", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class);
            tb.CreateType();
            ABuilder.Save(dummy_stub_dll_name);
        }

        HashSet<string> GetUses(TypeDef mt)
        {
            var mtypes = mt.GetTypes().ToList();
            mtypes.Add(mt);

            // whitelist verification
            var uses = new HashSet<string>();
            foreach (var mtype in mtypes)
            {
                foreach (var method in mtype.Methods.Where(m => m.HasBody && m.Body.HasInstructions))
                {
                    // quite possibly not exhaustive
                    foreach (var instruction in method.Body.Instructions.Where(ins => ins.GetOpCode() == dnlib.DotNet.Emit.OpCodes.Call || ins.GetOpCode() == dnlib.DotNet.Emit.OpCodes.Callvirt || ins.GetOpCode() == dnlib.DotNet.Emit.OpCodes.Calli || ins.GetOpCode() == dnlib.DotNet.Emit.OpCodes.Newobj))
                    {
                        if (instruction.Operand is IMemberRef)
                        {
                            var dc = (instruction.Operand as IMemberRef).DeclaringType;
                            while (dc != null)
                            {
                                if (dc == mt)
                                    break;
                                dc = dc.DeclaringType;
                            }
                            if (dc == mt)
                                continue;
                        }

                        uses.Add(instruction.Operand.ToString());
                    }
                }
            }

            return uses;
        }

        // yank the module out of the source assembly and smack it into a class inside its own assembly - just a small precaution so we dont load the entire source assembly with reflection
        bool VerifyAndRewriteModuleToDummy()
        {
            using (var src_mdd = ModuleDefMD.Load(InFile))
            {
                var mt = src_mdd.GlobalType;

                // dnlib doesnt like in memory reflection modules - they have no HINSTANCE
                using (var dummy_mdd = ModuleDefMD.Load(dummy_stub_dll_name))
                {
                    var dummyclass = dummy_mdd.Types.Single(t => !t.IsGlobalModuleType);

                    var uses = GetUses(mt);
                    //foreach (var s in uses)
                    //  System.Diagnostics.Debug.WriteLine("\"" + s + "\",");

                    if (!Unchecked)
                    {
                        var violation = uses.FirstOrDefault(u => !module_call_whitelist.Contains(u));
                        if (violation != null)
                        {
                            Console.WriteLine("Non-whitelisted call operand found in module: " + violation);
                            Console.WriteLine("Supply --unchecked to skip.");
                            Console.WriteLine("Aborting.");
                            return false;
                        }
                    }

                    foreach (var item in mt.Fields.ToList())
                    {
                        item.DeclaringType = null;
                        dummyclass.Fields.Add(item);
                    }

                    foreach (var item in mt.Methods.ToList())
                    {
                        item.DeclaringType = null;
                        dummyclass.Methods.Add(item);
                    }

                    foreach (var item in mt.NestedTypes.ToList())
                    {
                        item.DeclaringType = null;
                        dummyclass.NestedTypes.Add(item);
                    }

                    dummy_mdd.Write(module_out_dll_name);
                }
            }

            return true;
        }

        List<Instruction> CreateConstant(object value, TypeSig ret_type)
        {
            var new_ins = new List<Instruction>();

            if (value is string)
                new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldstr, value));
            else if (value is int) // I have yet to encounter this but whatever its free
                new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, value));
            else if (value is Array)
            {
                var arr = value as Array;
                var arrtype = arr.GetType().GetElementType();
                new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, arr.Length));

                var md = ret_type.Next.ToTypeDefOrRef();
                new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Newarr, md));
                for (int arri = 0; arri < arr.Length; arri++)
                {
                    new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Dup));
                    new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, arri));
                    if (arrtype == typeof(char))
                    {
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, (int)(char)arr.GetValue(arri)));
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Stelem_I2));
                    }
                    else if (arrtype == typeof(int))
                    {
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, arr.GetValue(arri)));
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Stelem_I4));
                    }
                    else if (arrtype == typeof(byte)) // untested but seems like a good one to have
                    {
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Ldc_I4, (int)(byte)arr.GetValue(arri)));
                        new_ins.Add(new Instruction(dnlib.DotNet.Emit.OpCodes.Stelem_I1));
                    }
                    else
                    {
                        Console.WriteLine("unable to handle array type: " + arrtype.FullName);
                        return null;
                    }
                }
            }

            return new_ins;
        }

        int HandleMethod(TypeDef mt, Type simulation, MethodDef method)
        {
            int replcount = 0;

            for (int i = method.Body.Instructions.Count - 1; i > 0; i--) // skip instruction 0 - it can never match
            {
                var ins = method.Body.Instructions[i];
                MethodSpec site;
                if (ins.GetOpCode() == dnlib.DotNet.Emit.OpCodes.Call && (site = ins.Operand as MethodSpec) != null && site.DeclaringType == mt)
                {
                    var prev = method.Body.Instructions[i - 1];
                    if (!prev.IsLdcI4())
                        continue;
                    var param = (uint)(int)prev.Operand;

                    var ret_type = site.GenericInstMethodSig.GenericArguments.Single();
                    if (!CanHandle(ret_type)) // note: just because we can handle it doesnt mean the code to emit the constant exists below :P
                    {
                        Console.WriteLine("unable to handle type: " + ret_type.FullName);
                        continue;
                    }

                    var omi = simulation.GetRuntimeMethods().Single(m => m.Name == site.Method.Name);

                    MethodInfo gen_method;
                    if (ret_type.IsSZArray && !ret_type.Next.IsCorLibType) // array of enum - we just treat it as ints and save ourself the hassle of bringing over the array type to the reflection assembly. enums can in theory also be based on other numeric types; this scenario is not handled
                        gen_method = omi.MakeGenericMethod(new Type[] { Type.GetType("System.Int32").MakeArrayType() });
                    else
                        gen_method = omi.MakeGenericMethod(new Type[] { Type.GetType(ret_type.FullName) });

                    var value = gen_method.Invoke(null, new object[] { param });

                    var new_ins = CreateConstant(value, ret_type);

                    if (new_ins != null && new_ins.Count > 0)
                    {
                        replcount++;

                        method.Body.SimplifyBranches(); // just in case short jumps will cease working

                        var old_to_new = new Dictionary<Instruction, Instruction>();

                        old_to_new[method.Body.Instructions[i - 1]] = new_ins[0];
                        method.Body.Instructions[i - 1] = new_ins[0];

                        old_to_new[method.Body.Instructions[i]] = new_ins[new_ins.Count - 1];
                        method.Body.Instructions.RemoveAt(i);

                        for (int ii = new_ins.Count - 1; ii > 0; ii--) // skip element 0
                            method.Body.Instructions.Insert(i, new_ins[ii]);

                        method.Body.UpdateInstructionOffsets();

                        FixUp(method, old_to_new);

                        method.Body.OptimizeBranches();
                    }
                }
            }

            return replcount;
        }

        void FixUp(MethodDef method, Dictionary<Instruction, Instruction> old_to_new)
        {
            // fix all references to the old instructions. really wish dnlib had a smart list for instruction tracking/referencing that we could leverage - or maybe I just cant find it again. write it?
            foreach (var cf in method.Body.Instructions.Where(ii => ii.Operand is Instruction || ii.Operand is Instruction[]))
            {
                var insa = cf.Operand as Instruction[];
                if (insa != null)
                {
                    for (int ii = 0; ii < insa.Length; ii++)
                        if (old_to_new.ContainsKey(insa[ii]))
                            insa[ii] = old_to_new[insa[ii]];
                }
                else if (old_to_new.ContainsKey(cf.Operand as Instruction))
                {
                    cf.Operand = old_to_new[cf.Operand as Instruction];
                }
            }
            foreach (var eh in method.Body.ExceptionHandlers)
            {
                if (old_to_new.ContainsKey(eh.HandlerStart))
                    eh.HandlerStart = old_to_new[eh.HandlerStart];
                if (old_to_new.ContainsKey(eh.HandlerEnd))
                    eh.HandlerEnd = old_to_new[eh.HandlerEnd];
                if (eh.FilterStart != null && old_to_new.ContainsKey(eh.FilterStart))
                    eh.FilterStart = old_to_new[eh.FilterStart];
                if (old_to_new.ContainsKey(eh.TryStart))
                    eh.TryStart = old_to_new[eh.TryStart];
                if (old_to_new.ContainsKey(eh.TryEnd))
                    eh.TryEnd = old_to_new[eh.TryEnd];
            }
        }

        public void DecryptAndSave(string outfile)
        {
            CreateDummy();

            if (!VerifyAndRewriteModuleToDummy())
                return;

            // we re-open src because we set some declaring types to things they shouldnt be in VerifyAndRewriteModuleToDummy()
            using (var src_mdd = ModuleDefMD.Load(InFile))
            {
                var mt = src_mdd.GlobalType;

                // load module assembly
                Assembly modass;
                using (var a = new BinaryReader(File.OpenRead(Path.GetFullPath(module_out_dll_name))))
                    modass = Assembly.Load(a.ReadBytes((int)a.BaseStream.Length)); // load through memory instead of file so we can delete it later
                var simulation = modass.ExportedTypes.Single();

                int replcount = 0;

                foreach (var type in src_mdd.GetTypes().Where(t => t != mt))
                {
                    // dont touch any types that are somehow contained in the module itself
                    var dc = type.DeclaringType;
                    while (dc != null)
                    {
                        if (dc == mt)
                            break;
                        dc = dc.DeclaringType;
                    }
                    if (dc == mt)
                        continue;

                    foreach (var method in type.Methods.Where(m => m.HasBody && m.Body.HasInstructions))
                    {
                        replcount += HandleMethod(mt, simulation, method);
                    }
                }

                Console.WriteLine("replaced " + replcount + " constants in " + Path.GetFileName(InFile) + ". Attempting to save..");
                try
                {
                    src_mdd.Write(outfile, new dnlib.DotNet.Writer.ModuleWriterOptions() { MetaDataOptions = new dnlib.DotNet.Writer.MetaDataOptions(dnlib.DotNet.Writer.MetaDataFlags.KeepOldMaxStack) });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAILED");
                    Console.WriteLine(ex.ToString());
                    Console.WriteLine();
                    Console.WriteLine();
                }
            }
        }

        private bool CanHandle(TypeSig ret_type)
        {
            if (ret_type.IsCorLibType)
                return true;

            if (ret_type.IsSZArray)
            {
                if (ret_type.Next.IsCorLibType)
                    return true; // not technically true - only char, int and byte(?) will work

                var vts = ret_type.Next as ValueTypeSig;

                if (vts != null && vts.TypeDef.BaseType.FullName.Contains("System.Enum"))
                    return true;
            }

            return false;
        }
    }
}
