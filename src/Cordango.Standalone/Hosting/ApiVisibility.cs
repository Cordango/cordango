// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Cordango.Standalone.Hosting;

/// <summary>
/// Makes the controllers visible to whatever is describing this application.
///
/// <para><b>Why it has to exist.</b> ASP.NET Core only describes a controller in an OpenAPI document
/// if its <c>ApiExplorer.IsVisible</c> is true, and the thing that normally sets it is
/// <c>[ApiController]</c>. This application deliberately does not use that attribute: it turns on
/// automatic model-state validation and body-binding inference, which would change how every
/// existing route answers a malformed request — a 400 in the framework's own shape rather than the
/// <c>{code, error}</c> one everything else here returns.</para>
///
/// <para>So the visibility is set on its own. Without this the document builds, serves, validates,
/// and contains <c>"paths": {}</c> — a completely empty API that looks like a configuration mistake
/// somewhere else entirely.</para>
///
/// <para><c>??=</c> rather than <c>=</c>: a controller that has said something about itself, with
/// <c>[ApiExplorerSettings]</c>, has said it deliberately and this must not overrule it.</para>
/// </summary>
internal sealed class ApiVisibility : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var controller in application.Controllers)
        {
            controller.ApiExplorer.IsVisible ??= true;

            foreach (var action in controller.Actions)
                action.ApiExplorer.IsVisible ??= true;
        }
    }
}
