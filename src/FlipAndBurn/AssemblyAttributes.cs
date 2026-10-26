// KSP reads these to order loading: MechJeb2 loads before us, and we are skipped if it is missing.
[assembly: KSPAssembly("FlipAndBurn", 0, 1)]
[assembly: KSPAssemblyDependency("MechJeb2", 2, 5)]
