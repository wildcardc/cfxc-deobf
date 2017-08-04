A ConfuserEx-custom deobfuscation toolchain
===========================================
To treat assemblies obfuscated with [yck1509's ConfuserEX](https://github.com/yck1509/ConfuserEx), with particular focus on Unity/Mono.
This toolchain is targeted at developers that more or less know what they are doing, and should provide you with fairly clean code to analyze.

No warranty, use at your own risk, yadayada. Licenses of libraries and tools used apply. This was done as a personal research project into obfuscation. Laws may apply that restrict usage of this tool.

Overview
--------
This package contains and describes in this README file:

1. A few pointers on how to use de4dot to prepare an obfuscated assembly.
2. A string and array constant unpacker that bakes ungainly method calls to the hidden <Module> back into ldstr/ldc on the IL level.
3. A deobfuscation method for the switch mangler employed for control flow obfuscation - including a modified yield return decompiler for Mono.

How to use
----------
### 1. Preparing and sanitizing the assembly
Grab a copy of [0xd4d's de4dot](https://github.com/0xd4d/de4dot/) (and optionally, if you got the source, build it), then run

    de4dot TARGET_ASSEMBLY -p un --un-name "!^_[0-9a-zA-Z]{26,28}$&^[a-zA-Z_<{$][a-zA-Z_0-9<>{}$.`-]*$"

The additional regex will prettify some symbol names, and generally this cleans up any unwieldy/invalid unicode in symbol names.

This should generate TARGET_ASSEMBLY-cleaned, which you will use for the following step.

### 2. Unpacking constants
#### WARNING
*This step will execute code in your target assembly via reflection!* If you have any doubt about the nature of it, do not risk this without looking at the code in its Module, and ensure it does nothing besides internal data manipulation.
There are measures in place that should in theory prevent the code from running rampart, but they may not be exhaustive.

Clone this repository if you haven't already, open up ConfuserExCustomModuleConstUnpacker.sln, ensure it can find and references dnlib, and build it.
If this gives you grief, skip ahead to step 3 where you build dnSpy, which in turn builds dnlib.dll in the process. You can now add a reference to this dll in the ConfuserExCustomModuleConstUnpacker project.

Run

    ConfuserExCustomModuleConstUnpacker TARGET_ASSEMBLY

If this step fails, you can skip it, look to other similar projects on GitHub (search for ConfuserEx), or try the --unchecked option at a considerable risk of arbitrary code execution. Or look at the code to see what the hell I'm doing wrong.

If it succeeds, it will generate TARGET_ASSEMBLY.unpacked.EXT, which you will use for the following step.

### 3. Patching dnSpy and exporting your assembly's code to a project
Start off by cloning [dnSpy](https://github.com/0xd4d/dnSpy) (assuming a git MINGW bash):

    git clone https://github.com/0xd4d/dnSpy
    cd dnSpy
    git submodule init
    git submodule update --recursive

Apply my patch, found in the root of this repository.

		cd Extensions/ILSpy.Decompiler/ICSharpCode.Decompiler
    git apply --ignore-whitespace ../../../../cfxc-deobf/icscdecomp_cfxc_deobf.patch

Your path to the patch may vary, of course. If it fails, you can first grab the revision this patch was made against via **git checkout af3940e**, or experiment with the **--3way** option.

Build and run (make sure you have NuGet and it restores packages), load up and select the assembly from the previous step, File->Export to Project. Done!

If this process breaks on Debugger.Break(), you found a scenario I didn't expect or cover. Happy hunting, or hit F5 and ignore it. You can comment those breaks if they annoy you.

### 4. The result
Some methods will still produce garbage or exceptions. Particularly yield return constructs are fickle.
You can use dnSpy's step-by-step ILAst generation to get a read on what's going on and perhaps manually extract code for such scenarios, there shouldn't be too many. Sometimes a vanilla version of dnSpy or ILSpy might also be the solution.

Sometimes you will be left with some compiler-generated, invalid symbol names. That usually also means something went awry, but sometimes just renaming them with a regex will do. Here' one for VS:

    <>f?__([a-zA-Z0-9]+)\$?([a-zA-Z0-9]*)
    $1$2
    
If you still get garbage control flow, a different method from what I target was employed. Good hunting! I hope my patch is somewhat comprehensible and buildable-upon. It's honestly pretty hackish. Poor you.

Appendix: Author's ramblings
----------------------
Huge shoutouts to the ILspy developers (particularly, whoever created the ILAst and Decompiler bits), 0xd4d and yck1509. I mostly did this out of intellectual curiosity, and to see what was inside certain Unity assemblies. Most ConfuserEx unpackers don't tackle the control-flow obfuscation, and I found it
a great challenge. I also got yield-return to work (mostly) and fixed some bugs/false assumptions in the decompilation process.

It should be noted that despite ConfuserEx being open source, I did 80% of this with a black box approach. I did at some point cave
and sneaked a peek at its source, and perhaps a bit of inspiration came from that, but I was very close to a working approach then.

Others have tackled the 2nd step of my package, but I didn't really like their approaches, so I made my own. I can't rightly claim I tested any of the stuff on github. It might do a better job.

#### Wishlist
* Rewrite the yield return decompiler entirely - it's a little too narrow to catch many obfuscation scenarios, though for Mono, I think I did an okay job. What's missing is mostly documented in my patch.
* Yield-return for platforms other than Mono. Ties in with the above.
* Getting this in an all-in-one package... yea probably not happening.
* Submit what I believe to be actual improvements to ICSharpCode.Decompiler.
* Make Deobf a discernible and "skippable" entry for dnSpy.
* Code quality.
* More constant types supported in ConfuserExCustomModuleConstUnpacker.

#### Why..?
###### Why a patch instead of a fork?
It seemed more appropriate to me, and i dislike forks that also add random stuff, as I would have had to do here.
###### Why do this as part of dnSpy instead of something much more obvious, like de4dot?
I liked ILAst a lot for this, which is not innately part of de4dot. I liked that it did a control flow graph for me. I liked that I could take a look at the ILAst at different stages to figure out what was going on. But you're right, an all-in-one solution would have been better.

#### Bonus
Here is a fun bit of valid IL that decompiles to invalid C# I encountered along the way:

###### ILAst

    ceq:bool(
        ldfld:SomeEnum(var_0, ldloc:T(this)),
        and:SomeEnum(
         ldfld:SomeEnum(var_0, ldloc:T(this)),
         neg:SomeEnum(ldfld:SomeEnum(var_0, ldloc:T(this)))
      )
    )
    
###### C#

    [Flags]
    enum SomeEnum
    {
        ...
    }
    
    ...
    
    SomeEnum e;
    ...
    if (e == (e & -e))
        ...
    
Now, negation is not a valid operation on enums, even Flags, but while IL doesn't care about that, the C# compiler does.
For the curious, this check is used, when iterating all members of an enum, to exclude elements of an enum that have more than one bit set, which is to say, bit masks!
You can fix this by first casting the enum to an integer type.
