//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Xamarin.Interactive.Logging;

using Xamarin.Interactive.CodeAnalysis.Resolving;

namespace Xamarin.Interactive.CodeAnalysis.Evaluating
{
    public sealed class EvaluationAssemblyContext : IDisposable
    {
        // Implement a custom AssemblyName comparer so that we don't have to insert
        // multiple different varieties of the same assembly name into the dictionary.
        // Different pieces of external code seem to look up our submission assemblies in
        // different ways: JSON.NET uses bare names (see https://bugzilla.xamarin.com/show_bug.cgi?id=58801),
        // most of the framework uses fully qualified assembly names, and ASP.NET Core
        // seems to use fully-qualified-except-no-version names. As submission assemblies
        // aren't versioned, don't have a culture, and don't have a public key token, treating
        // the name in a case insensitive way is fine.
        sealed class AssemblyNameInsensitiveNameOnlyComparer : IEqualityComparer<AssemblyName>
        {
            public static bool Equals (string x, string y)
                => string.Equals (x, y, StringComparison.OrdinalIgnoreCase);

            public static bool Equals (AssemblyName x, AssemblyName y)
                => Equals (x?.Name, y?.Name);

            public static readonly IEqualityComparer<AssemblyName> Default
                = new AssemblyNameInsensitiveNameOnlyComparer ();

            bool IEqualityComparer<AssemblyName>.Equals (AssemblyName x, AssemblyName y)
                => Equals (x?.Name, y?.Name);

            int IEqualityComparer<AssemblyName>.GetHashCode (AssemblyName obj)
                => obj?.Name == null ? 0 : obj.Name.GetHashCode ();
        }

        const string TAG = nameof (EvaluationAssemblyContext);

        readonly Dictionary<string, AssemblyDefinition> assemblyMap
            = new Dictionary<string, AssemblyDefinition> (
                StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<AssemblyName, Assembly> netAssemblyMap
            = new Dictionary<AssemblyName, Assembly> (
                AssemblyNameInsensitiveNameOnlyComparer.Default);

        public Action<Assembly, AssemblyDefinition> AssemblyResolvedHandler { get; }

        public EvaluationAssemblyContext (Action<Assembly, AssemblyDefinition> assemblyResolvedHandler = null)
        {
            AssemblyResolvedHandler = assemblyResolvedHandler;
            AppDomain.CurrentDomain.AssemblyResolve += HandleAssemblyResolve;
        }

        public void Dispose ()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= HandleAssemblyResolve;
            GC.SuppressFinalize (this);
        }

        Assembly HandleAssemblyResolve (object sender, ResolveEventArgs args)
        {
            Log.Verbose (TAG, $"Handling assembly resolve event for {args.Name}.");

            Assembly netAssembly;
            Log.Verbose (TAG, $"Looking up {args.Name} in compiled assembly map.");
            if (netAssemblyMap.TryGetValue (new AssemblyName (args.Name), out netAssembly))
                return netAssembly;

            AssemblyDefinition assembly;
            var shortName = new AssemblyName (args.Name).Name;

            Log.Verbose (TAG, $"Looking up {shortName} in assembly definition map.");
            if (assemblyMap.TryGetValue (shortName, out assembly)) {
                if (args.RequestingAssembly?.ReflectionOnly == true) {
                    if (File.Exists (assembly.Content.Location))
                        return Assembly.ReflectionOnlyLoadFrom (assembly.Content.Location);

                    if (assembly.Content.PEImage != null)
                        return Assembly.ReflectionOnlyLoad (assembly.Content.PEImage);

                    Log.Verbose (
                        TAG,
                        $"Could not reflection-only load assembly {args.Name}, location {assembly.Content.Location} " +
                        "did not exist and PEImage was not sent.");

                    return null;
                }

                Assembly loadedAsm;

                if (File.Exists (assembly.Content.Location))
                    loadedAsm = Assembly.LoadFrom (assembly.Content.Location);
                else if (assembly.Content.PEImage != null) {
                    if (assembly.Content.DebugSymbols != null)
                        loadedAsm = Assembly.Load (
                            assembly.Content.PEImage,
                            assembly.Content.DebugSymbols);
                    else
                        loadedAsm = Assembly.Load (assembly.Content.PEImage);
                } else
                    loadedAsm = null;

                if (loadedAsm == null)
                    Log.Verbose (
                        TAG,
                        $"Could not load assembly {args.Name}, location {assembly.Content.Location} did not " +
                        "exist and PEImage was not sent.");

                AssemblyResolvedHandler?.Invoke (loadedAsm, assembly);
                return loadedAsm;
            }

            Log.Verbose (TAG, $"Could not find {args.Name} in any assembly map.");

            return null;
        }

        public void AddRange (IEnumerable<AssemblyDefinition> assemblies)
        {
            if (assemblies == null)
                throw new ArgumentNullException (nameof (assemblies));

            foreach (var assembly in assemblies) {
                if (assembly != null)
                    Add (assembly);
            }
        }

        public void Add (AssemblyDefinition assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException (nameof (assembly));

            assemblyMap [assembly.Name.Name] = assembly;
        }

        public void Add (Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException (nameof (assembly));

            netAssemblyMap [assembly.GetName ()] = assembly;
        }
    }
}