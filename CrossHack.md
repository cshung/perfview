# CrossGenerationLivenessCollector

## Overview
CrossGenerationLivenessCollector was a tool developed long time ago to capture and analyze promoted bytes spikes. This can be observed in GCStats and can be analyzed using this tool. For a while, the tool is not compiled as part of the product. This branch brings it back. Its current status is unofficial, and will probably stay that way due to its nature. (It requires embedding WinDBG binaries into PerfView and it also requires Microsoft internal CLR symbols.)

## What is promoted bytes?
When GC performs a gen 0 or gen 1 collection, objects that survives (i.e. still being referenced transitively by a root) will be promoted to the next generation. The total number of bytes that get promoted is called 'promoted bytes'.

## What does a spike in promoted bytes mean?
In a typical server application. We assume there are two types of data, either it is per request (i.e. objects that are allocated when a request comes and become unused when the request finishes) or that is per application (it lives in the application practically forever, such as database caches). When there is a sudden increase in promoted bytes, it could be the case that some per request objects got accidentally rooted by a per application object. Gen 2 objects are long term debt. They are going to cause worst case latency (i.e. blocking gen 2) to become longer. Regardless of the reason of the promoted bytes spike, we would like to understand why.

## What can the tool do?
The tool can stay attached to a process to wait until it detects a promoted bytes spike, and when that happen it will capture a dump at the point for analysis.

## What can the analysis do?
To be honest, my knowledge in the analysis is limited. Here is how I interpreted the code. PerfView is primarily a profiling tool, it has a nice aggregated stack viewer. If all you have is a hammer, all you will see is a nail. Reading through the code, it looks like the code is converting the object graph by aggregating the objects by type, by generation, and then convert that graph into a tree, and finally represent it on a stack viewer. We can reasonably interpret the parent node of the tree is directly referencing its child, but some referencing relationship is gone because a tree cannot have cycle.

I am thinking that by not aggregating objects of different generations together, the code produces a generation aware view, as we will see later in the howto section.

## Current limitations
- Desktop CLR only (would love to lift to any .NET offering)
- Private clr.pdb required (would love to eliminate this requirement)
- Associated mscordacwks.dll required
- It creates a gcdump file with cross generational analysis enabled.

## Prerequisite
Obtain a PerfView.exe built from this branch.

## Howto Capture
Run a desktop CLR application, for now, we run DesktopWorkspace.exe

```C#
namespace DesktopWorkspace
{
    using System;
    using System.Diagnostics;

    class Gen2
    {
        public Gen0 leak;
    }

    class Gen0
    {
        public Gen0 prev;
        public byte[] weight;
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("My process id is " + Process.GetCurrentProcess().Id);
            Console.ReadLine();
            Gen2 gen2 = new Gen2();
            GC.Collect();
            GC.Collect();
            int counter = 0;
            while (true)
            {
                Gen0 head = new Gen0();
                for (int i = 0; i < 1000; i++)
                {
                    Gen0 next = new Gen0();
                    next.weight = new byte[1000];
                    next.prev = head;
                    head = next;
                }
                counter++;
                // Emulate a leak of a ephermal object to gen2.
                if (counter % 1000 == 0)
                {
                    Console.WriteLine("Leaked");
                    gen2.leak = head;
                }
            }
        }
    }
}
```
Consider the `while (true)` loop begin processing requests. It creates a link list of 1000 nodes, each of the node worth about 1000 bytes. So each request produce about 1,000,000 bytes. But they are gone in the next iteration. Once in a while, when counter is a multiple of 1000, it leaks into an object named Gen2. The goal is to figure out this leak.

When that happens, normally, the GC handles around 1,000,000 bytes. But when the leak happens, it should handle up to 2,000,000 bytes. To capture that, we want to define a threshold for 1,500,000. 

Now launch the PerfView.exe we just built.
Click on the File -> User Command
Type in `CollectCrossGenerationLiveness 22988 1 1500000 c:\temp\DesktopWorkspace.gcdump`

`22988` is the process ID of DesktopWorkspace.exe 
`1` means we wanted to stop on a gen 1 GC.
`1500000` means if the gen 1 GC promotes more than 1500000 bytes, then this is the time when we wanted to stop.

## Deal with symbols

It is possible (in fact it always happen for me) to have this error:
```
...
DBGHELP: clr - public symbols  
        C:\Users\andrewau\AppData\Roaming\PerfView\VER.2020-04-16.15.23.52.993\x86\sym\clr.pdb\3BECA8AF9F8A48C49A25F0C0385F447E2\clr.pdb

HeapDump Error (-2146233088): System.Exception: CLR symbols are not properly loaded.  Please set the symbol path and try again.
...
```
To understand this error, we need to know about public and private symbols. Debug symbols generated by the compiler typically contains a lot of information. This is too much for the public, therefore, it is typically stripped before it is published. The pre-stripped version is called private symbols, the stripped one is called public symbols.

This tools requires the private symbols.  If we see an error similar to the one above, you need to replace it with a private one. This is feasible only for Microsoft internal people who have access to the private symbols.

With luck, you may see something similar or different. The `3BECA8AF9F8A48C49A25F0C0385F447E2` is a hash of my clr.dll, depending on version you might get a different string, you maybe able to get to the private symbols directly (if the network somehow allows), or you don't even get the public symbol. Bottom line is that the tool requires the private symbol of clr.dll placed right at that location. 

If it is done right, you should not see ` - public symbols` in the log anymore. Instead you should see ` - private symbols`.

For Microsoft internal people, you could obtain the private symbols by debugging a process and pointing to our internal symbol server. After doing that, I copied my clr.pdb from my symbol cache there

```
copy /y c:\debuggers\x86\sym\clr.pdb\3BECA8AF9F8A48C49A25F0C0385F447E2\clr.pdb C:\Users\andrewau\AppData\Roaming\PerfView\VER.2020-04-16.15.23.52.993\x86\sym\clr.pdb\3BECA8AF9F8A48C49A25F0C0385F447E2\
```

Now the command should go through and we will get to the gcdump file.

## Howto View
To perform the analysis, we can open the generated gcdump file in PerfView. When we double click the gcdump file, there should be 3 views showing up:

- Heap Stacks:
- Advanced Group: Gen0 Walkable Object Stacks
- Advanced Group: Gen1 Walkable Object Stacks

> If you don't see these knobs, you are probably running it on a public computer that cannot access a computer named `clrmain`. If so, you can hack away that restriction by setting AppLog.s_InternalUser to true in a managed debugger.

The heap stack view is an overall aggregation, objects of the same type, regardless of generation, are all aggregated together.
The Gen0 Walkable object stacks *probably* only show objects of gen 0.
The Gen1 Walkable object stacks *probably* only show objects of gen 1, but as per my experimentation, it seems to also show gen 0.

Now open the The Gen1 Walkable object stacks. After opening the view, go to the toolbar and delete the default fold value of 1 to avoid information being folded away. The key problem is that there is only a single object named `Gen2`, and PerfView thinks it is insignificant and hide it from your view. When that is deleted, you should be able to see an object named `Gen2` is holding onto objects called `Gen0` and together they represents a significant proportion of the heap.
