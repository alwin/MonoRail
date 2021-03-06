﻿//  Copyright 2004-2012 Castle Project - http://www.castleproject.org/
//  Hamilton Verissimo de Oliveira and individual contributors as indicated. 
//  See the committers.txt/contributors.txt in the distribution for a 
//  full listing of individual contributors.
// 
//  This is free software; you can redistribute it and/or modify it
//  under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 3 of
//  the License, or (at your option) any later version.
// 
//  You should have received a copy of the GNU Lesser General Public
//  License along with this software; if not, write to the Free
//  Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
//  02110-1301 USA, or see the FSF site: http://www.fsf.org.

namespace Castle.MonoRail

    open System
    open System.IO
    open System.Collections.Generic
    open System.Net
    open System.Web
    open Castle.MonoRail.ViewEngines

    /// Returns just a http status code and optionally a description. 
    /// Useful for returns of verbs like HEAD, or requests with headers such as If-Modified-Since
    type HttpResult<'TModel>(status:HttpStatusCode) = 
        inherit ActionResult<'TModel>()

        let mutable _statusCode = status
        let mutable _status : string = null
        let mutable _statusDesc : string = null

        member x.StatusCode with get() = _statusCode and set(v) = _statusCode <- v
        member x.Status     with get() = _status and set(v) = _status <- v
        member x.StatusDescription with get() = _statusDesc and set(v) = _statusDesc <- v

        override this.Execute(context:ActionResultContext) = 
            let response = context.HttpContext.Response
            response.StatusCode <- int(_statusCode)
            if not (String.IsNullOrEmpty(_status)) then
                response.Status <- _status
            if not (String.IsNullOrEmpty(_statusDesc)) then
                response.StatusDescription <- _statusDesc

    type HttpResult(status:HttpStatusCode) = 
        inherit HttpResult<obj>(status) 

    /// No operation is executed after the action is run. 
    type EmptyResult private () = 
        inherit ActionResult()

        static let _instance = EmptyResult()

        static member Instance = _instance

        override this.Execute(context:ActionResultContext) = 
            ()
        

    type RedirectResult(url:string) = 
        inherit ActionResult()

        new(url:TargetUrl) = RedirectResult((url.Generate null))

        override this.Execute(context:ActionResultContext) = 
            context.HttpContext.Response.Redirect (url, false)


    type PermRedirectResult(url:string) = 
        inherit ActionResult()

        new(url:TargetUrl) = PermRedirectResult((url.Generate null))

        override this.Execute(context:ActionResultContext) = 
            context.HttpContext.Response.RedirectPermanent (url, false)


    type ViewResult<'TModel>(model:'TModel, bag:PropertyBag<'TModel>) = 
        inherit HttpResult<'TModel>(HttpStatusCode.OK)

        let mutable _viewName : string = null
        let mutable _layoutName : string = null
        let mutable _model = model
        let mutable _propBag = bag

        new (bag:PropertyBag<'TModel>) =
            ViewResult<'TModel>(bag.Model, bag) 
        new (model:'TModel) =
            ViewResult<'TModel>(model, PropertyBag<'TModel>()) 

        member x.Model  with get() = _model   and set v = _model <- v
        member x.Bag    with get() = _propBag and set v = _propBag <- v 
        member x.ViewName  with get() = _viewName and set v = _viewName <- v
        member x.LayoutName  with get() = _layoutName and set v = _layoutName <- v

        override this.Execute(context:ActionResultContext) = 
            base.Execute(context)
            let viewreq = 
                ViewRequest ( 
                                ViewName = this.ViewName, 
                                GroupFolder = context.ControllerDescriptor.Area,
                                ViewFolder = context.ControllerDescriptor.Name,
                                OuterViewName = this.LayoutName,
                                DefaultName = context.ActionDescriptor.Name
                            )
            let reg = context.ServiceRegistry
            reg.ViewRendererService.Render(viewreq, context.HttpContext, _propBag, _model)

        interface IModelAccessor<'TModel> with 
            member x.Model = _model


    type ViewResult() = 
        inherit ViewResult<obj>(obj())


    [<AbstractClass>]
    type SerializerBaseResult<'TModel>(contentType:string, model:'TModel) = 
        inherit HttpResult<'TModel>(HttpStatusCode.OK)

        abstract GetMimeType : unit -> MimeType

        override this.Execute(context:ActionResultContext) = 
            base.Execute(context)
            context.HttpContext.Response.ContentType <- contentType

            let serv = context.ServiceRegistry
            let metaProvider = serv.ModelMetadataProvider

            let found, processor = serv.ModelHypertextProcessorResolver.TryGetProcessor<'TModel>()
            if found then
                processor.AddHypertext model

            let mime = this.GetMimeType()
            let serializer = serv.ModelSerializerResolver.CreateSerializer<'TModel>(mime)
            if (serializer != null) then
                serializer.Serialize (model, contentType, context.HttpContext.Response.Output, metaProvider)
            else 
                failwithf "Could not find serializer for contentType %s and model %s" contentType (typeof<'TModel>.Name)

        interface IModelAccessor<'TModel> with 
            member x.Model = model


    type JsonResult<'TModel when 'TModel : not struct>(contentType:string, model:'TModel) = 
        inherit SerializerBaseResult<'TModel>(contentType, model)

        new (model:'TModel) = 
            JsonResult<'TModel>("application/json", model)

        override x.GetMimeType () = MimeType.JSon

    // non generic version. useful for anonymous types
    type JsonResult(contentType:string, model:obj) = 
        inherit JsonResult<obj>(contentType, model)

        new (model:obj) = 
            JsonResult("application/json", model)

        override x.GetMimeType () = MimeType.JSon


    type JsResult<'TModel when 'TModel : not struct>(contentType:string, model:'TModel) = 
        inherit SerializerBaseResult<'TModel>(contentType, model)

        new (model:'TModel) = 
            JsResult<'TModel>("application/javascript", model)

        override x.GetMimeType () = MimeType.Js


    (*
    type FileResult() = 
        inherit ActionResult()
        override this.Execute(context:ActionResultContext) = 
            ignore()
    *)


    type XmlResult<'TModel when 'TModel : not struct>(contentType:string, model:'TModel) = 
        inherit SerializerBaseResult<'TModel>(contentType, model)

        new (model:'TModel) = 
            XmlResult<'TModel>("text/xml", model)

        override x.GetMimeType () = MimeType.Xml


    type ContentNegotiatedResult<'TModel when 'TModel : not struct>(model:'TModel, bag:PropertyBag<'TModel>) = 
        inherit HttpResult<'TModel>(HttpStatusCode.OK)

        let mutable _redirectTo : TargetUrl = null
        let mutable _location : TargetUrl = null
        let mutable _locationUrl : string = null
        let _actions = lazy Dictionary<MimeType,Func<ActionResult>>()

        new (bag:PropertyBag<'TModel>) =
            ContentNegotiatedResult<'TModel>(bag.Model, bag) 
        new (model:'TModel) =
            ContentNegotiatedResult<'TModel>(model, PropertyBag<'TModel>()) 

        member x.RedirectBrowserTo  with get() = _redirectTo and set v = _redirectTo <- v
        member x.Location           with get() = _location and set v = _location <- v
        member x.LocationUrl        with get() = _locationUrl and set v = _locationUrl <- v
        
        member this.When(``type``:MimeType, perform:Func<ActionResult>) = 
            _actions.Force().[``type``] <- perform
            this

        override this.Execute(context:ActionResultContext) = 
            let serv = context.ServiceRegistry
            let mime = serv.ContentNegotiator.ResolveRequestedMimeType context.RouteMatch (context.HttpContext.Request)
            this.InternalExecute mime context

            base.Execute(context)

        member internal x.InternalExecute mime context = 
            let response = context.HttpContext.Response

            if _locationUrl != null then 
                response.RedirectLocation <- _locationUrl
            elif _location != null then 
                response.RedirectLocation <- _location.Generate null

            let hasCustomAction, func = 
                if _actions.IsValueCreated 
                then _actions.Value.TryGetValue mime 
                else false, null

            if hasCustomAction then // customized one found
                let result = func.Invoke()
                // todo: Assert it was created
                result.Execute(context)

            else // run standard one

                let result : ActionResult = 
                    match mime with 
                    | MimeType.Atom -> upcast XmlResult<'TModel>("application/atom+xml", model)
                    | MimeType.JSon -> upcast JsonResult<'TModel>(model)
                    | MimeType.Js -> upcast JsResult<'TModel>(model)
                    | MimeType.Rss -> upcast XmlResult<'TModel>("application/rss+xml", model)
                    | MimeType.Html -> 
                        if _redirectTo <> null then
                            upcast RedirectResult(_redirectTo)
                        else
                            upcast ViewResult<'TModel>(model, bag)
                    | MimeType.Xml -> upcast XmlResult<'TModel>("text/xml", model)
                    | _ -> failwithf "Could not process mime type %s" (mime.ToString())
                result.Execute(context)

        interface IModelAccessor<'TModel> with 
            member x.Model = model


    type ContentNegotiatedResult(bag:PropertyBag) = 
        inherit ContentNegotiatedResult<obj>(bag)

        new() = ContentNegotiatedResult(null)


    type OutputWriterResult(writing:Action<Stream>) = 
        inherit HttpResult(HttpStatusCode.OK)

        override this.Execute(context:ActionResultContext) = 
            let stream = context.HttpContext.Response.OutputStream
            writing.Invoke(stream)
