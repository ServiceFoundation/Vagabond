﻿namespace System
open System.Reflection

[<assembly: AssemblyVersionAttribute("0.2.3")>]
[<assembly: AssemblyFileVersionAttribute("0.2.3")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.2.3"
