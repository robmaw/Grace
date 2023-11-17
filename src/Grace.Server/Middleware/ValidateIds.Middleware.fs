﻿namespace Grace.Server.Middleware

open Dapr.Actors
open Giraffe.HttpStatusCodeHandlers.RequestErrors
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.AspNetCore.Http.HttpResults
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Linq
open System.Reflection
open System.Text
open System.Threading.Tasks
open Giraffe.Core
open System.Text.Json

/// Holds the PropertyInfo for each Entity Id and Name property.
type EntityProperties =
    {
        OwnerId: PropertyInfo option
        OwnerName: PropertyInfo option
        OrganizationId: PropertyInfo option
        OrganizationName: PropertyInfo option
        RepositoryId: PropertyInfo option
        RepositoryName: PropertyInfo option
        BranchId: PropertyInfo option
        BranchName: PropertyInfo option
    }

    static member Default =
        {
            OwnerId = None
            OwnerName = None
            OrganizationId = None
            OrganizationName = None
            RepositoryId = None
            RepositoryName = None
            BranchId = None
            BranchName = None
        }

/// Examines the body of the incoming request to validate the Ids and Names in the request, and ensure that we know the right Ids. Having the Ids already figured out saves work for the rest of the pipeline.
///
/// If the Ids are invalid, it returns 400 Bad Request.
///
/// If the Ids and/or Names aren't found, it returns 404 Not Found.
type ValidateIdsMiddleware(next: RequestDelegate) =

    let log = ApplicationContext.loggerFactory.CreateLogger("ValidateIdsMiddleware")

    /// Holds the parameter type for each endpoint.
    let typeLookup = ConcurrentDictionary<String, Type>()

    /// Holds the property info for each parameter type.
    let propertyLookup = ConcurrentDictionary<Type, EntityProperties>()

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let getBodyType (context: HttpContext) = 
        let path = context.Request.Path.ToString()
        if not <| path.StartsWith("/healthz") && not <| path.StartsWith("/actors") && not <| path.StartsWith("/dapr") then
            let endpoint = context.GetEndpoint()
            if isNull(endpoint) then
                log.LogDebug("Path: {context.Request.Path}; Endpoint: null.", context.Request.Path)
                None
            elif endpoint.Metadata.Count > 0 then
                //logToConsole $"Path: {context.Request.Path}; endpoint.Metadata.Count: {endpoint.Metadata.Count}."
                //endpoint.Metadata |> Seq.iter (fun m -> logToConsole (sprintf "%A: %A" m (m.GetType())))
                let requestBodyType = endpoint.Metadata 
                                        |> Seq.tryFind (fun m -> m.GetType().FullName = "System.RuntimeType") 
                                        |> Option.map (fun m -> m :?> Type)
                if requestBodyType |> Option.isSome then log.LogDebug("Path: {context.Request.Path}; Endpoint: {endpoint.DisplayName}; RequestBodyType: {requestBodyType.Value.Name}.", context.Request.Path, endpoint.DisplayName, requestBodyType.Value.Name)
                requestBodyType
            else
                log.LogDebug("Path: {context.Request.Path}; endpoint.Metadata.Count = 0.", context.Request.Path)
                None            
        else
            None

    member this.Invoke(context: HttpContext) =
        task {

    // -----------------------------------------------------------------------------------------------------
    // On the way in...
#if DEBUG
            let startTime = getCurrentInstant()
            let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"];
            context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof(ValidateIdsMiddleware)} --> ";
#endif

            let path = context.Request.Path.ToString()
            let mutable requestBodyType: Type = null
            let mutable graceIds = GraceIds.Default
            let mutable notFound = false
            let mutable badRequest = false

            // If we haven't seen this endpoint before, get the parameter type for the endpoint.
            if not <| typeLookup.TryGetValue(path, &requestBodyType) then
                match getBodyType context with
                | Some t -> 
                    requestBodyType <- t
                    typeLookup.TryAdd(path, requestBodyType) |> ignore
                | None ->
                    typeLookup.TryAdd(path, null) |> ignore

            // If we have a parameter type for the endpoint, parse the body of the request to get the Ids and Names.
            // If we don't have a parameter type for the endpoint, it's an endpoint like /healthz or whatever.
            if not <| isNull(requestBodyType) then
                context.Request.EnableBuffering()
                match! context |> parseType requestBodyType with
                | Some requestBody ->
                    // Get the available entity properties for this endpoint from the dictionary.
                    //   If we don't already have them, figure out which properties are available for this type, and cache that.
                    let mutable entityProperties: EntityProperties = EntityProperties.Default
                    if not <| propertyLookup.TryGetValue(requestBodyType, &entityProperties) then
                        // We haven't seen this request body type before, so we need to figure out which properties are available.

                        // Get all of the properties on the request body type.
                        let properties = requestBodyType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                        let findProperty name = properties |> Seq.tryFind(fun p -> p.Name = name)

                        // Check if these indivudal properties exist on the request body type.
                        entityProperties <-
                            {
                                OwnerId =           findProperty (nameof(OwnerId))
                                OwnerName =         findProperty (nameof(OwnerName))
                                OrganizationId =    findProperty (nameof(OrganizationId))
                                OrganizationName =  findProperty (nameof(OrganizationName))
                                RepositoryId =      findProperty (nameof(RepositoryId))
                                RepositoryName =    findProperty (nameof(RepositoryName))
                                BranchId =          findProperty (nameof(BranchId))
                                BranchName =        findProperty (nameof(BranchName))
                            }

                        // Cache the results.
                        propertyLookup.TryAdd(requestBodyType, entityProperties) |> ignore

                    // let sb = StringBuilder()
                    // properties |> Array.iter (fun p -> sb.Append($"{p.Name}; ") |> ignore)
                    // logToConsole $"Path: {context.Request.Path}; Properties: {sb.ToString()}."

                    let mutable ownerId = String.Empty
                    let mutable ownerName = String.Empty
                    let mutable organizationId = String.Empty
                    let mutable organizationName = String.Empty
                    let mutable repositoryId = String.Empty
                    let mutable repositoryName = String.Empty
                    let mutable branchId = String.Empty
                    let mutable branchName = String.Empty
                    
                    log.LogDebug("Request body: {requestBody}", serialize requestBody)
                    context.Items.Add("", requestBody)

                    // Get Owner information.
                    if Option.isSome entityProperties.OwnerId && Option.isSome entityProperties.OwnerName then
                        // Get the values from the request body.
                        ownerId <- entityProperties.OwnerId.Value.GetValue(requestBody) :?> string
                        ownerName <- entityProperties.OwnerName.Value.GetValue(requestBody) :?> string

                        // Resolve the OwnerId based on the provided Id and Name.
                        match! resolveOwnerId ownerId ownerName with
                        | Some resolvedOwnerId ->
                            // Check to see if the Owner exists.
                            match! Owner.ownerExists resolvedOwnerId ownerName Owner.OwnerError.OwnerDoesNotExist with
                            | Ok _ ->
                                graceIds <- {graceIds with OwnerId = resolvedOwnerId; HasOwner = true}
                            | Error error ->
                                notFound <- true
                        | None ->
                            badRequest <- true

                    // Get Organization information.
                    if Option.isSome entityProperties.OrganizationId && Option.isSome entityProperties.OrganizationName then
                        // Get the values from the request body.
                        organizationId <- entityProperties.OrganizationId.Value.GetValue(requestBody) :?> string
                        organizationName <- entityProperties.OrganizationName.Value.GetValue(requestBody) :?> string

                        // Resolve the OrganizationId based on the provided Id and Name.
                        match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                        | Some resolvedOrganizationId ->
                            // Check to see if the Organization exists.
                            match! Organization.organizationExists ownerId ownerName resolvedOrganizationId organizationName Organization.OrganizationError.OrganizationDoesNotExist with
                            | Ok _ ->
                                graceIds <- {graceIds with OrganizationId = resolvedOrganizationId; HasOrganization = true}
                            | Error error ->
                                notFound <- true
                        | None -> 
                            badRequest <- true

                    // Get repository information.
                    if Option.isSome entityProperties.RepositoryId && Option.isSome entityProperties.RepositoryName then
                        // Get the values from the request body.
                        repositoryId <- entityProperties.RepositoryId.Value.GetValue(requestBody) :?> string
                        repositoryName <- entityProperties.RepositoryName.Value.GetValue(requestBody) :?> string

                        // Resolve the RepositoryId based on the provided Id and Name.
                        match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                        | Some resolvedRepositoryId ->
                            // Check to see if the Repository exists.
                            match! Repository.repositoryExists ownerId ownerName organizationId organizationName resolvedRepositoryId repositoryName Repository.RepositoryError.RepositoryDoesNotExist with
                            | Ok _ ->
                                graceIds <- {graceIds with RepositoryId = resolvedRepositoryId; HasRepository = true}
                            | Error error ->
                                notFound <- true
                        | None ->
                            badRequest <- true

                    // Get branch information.
                    if Option.isSome entityProperties.BranchId && Option.isSome entityProperties.BranchName then
                        // Get the values from the request body.
                        branchId <- entityProperties.BranchId.Value.GetValue(requestBody) :?> string
                        branchName <- entityProperties.BranchName.Value.GetValue(requestBody) :?> string

                        // Resolve the BranchId based on the provided Id and Name.
                        match! resolveBranchId graceIds.RepositoryId branchId branchName with
                        | Some resolvedBranchId ->
                            // Check to see if the Branch exists.
                            match! Branch.branchExists ownerId ownerName organizationId organizationName repositoryId repositoryName resolvedBranchId branchName Branch.BranchError.BranchDoesNotExist with
                            | Ok _ ->
                                graceIds <- {graceIds with BranchId = resolvedBranchId; HasBranch = true}
                            | Error error ->
                                notFound <- true
                        | None ->
                            badRequest <- true

                    context.Items.Add(nameof(GraceIds), graceIds)
                | None ->
                    ()

                // Reset the Body to the beginning so that it can be read again later in the pipeline.
                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore
                
            if badRequest then
                context.Response.StatusCode <- 400
                do! context.Response.WriteAsync("The provided entity Id's and/or Names are invalid.")
                do! Task.CompletedTask
            elif notFound then
                context.Response.StatusCode <- 400
                do! context.Response.WriteAsync("The provided entity Id's and/or Names were not found in the database.")
                do! Task.CompletedTask
            else
// -----------------------------------------------------------------------------------------------------

                // Pass control to next middleware instance...
                let nextTask = next.Invoke(context);

// -----------------------------------------------------------------------------------------------------
// On the way out...

#if DEBUG
                let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"];
                context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof(ValidateIdsMiddleware)} --> ";

                let elapsed = getCurrentInstant().Minus(startTime).TotalMilliseconds
                if not <| path.StartsWith("/healthz") && not <| path.StartsWith("/actors") && not <| path.StartsWith("/dapr") then
                    log.LogDebug("{currentInstant}: Path: {path}; Elapsed: {elapsed}ms; Context.Items: {context.Items}",
                        getCurrentInstantExtended(), context.Request.Path, elapsed, serialize context.Items)
#endif
                do! nextTask
        } :> Task
