// KSP reads these to order loading: MechJeb2 loads before us, and we are skipped if it is missing or older than 2.15.
[assembly: KSPAssembly("FlipAndBurn", 1, 0)]
[assembly: KSPAssemblyDependency("MechJeb2", 2, 15)]
