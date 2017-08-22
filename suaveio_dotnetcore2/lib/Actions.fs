namespace RealWorld.Effects

module Actions = 
  open Suave
  open RealWorld.Models
  open DB
  open MongoDB.Bson
  open System.Text
  open System
  open RealWorld
  open Suave.RequestErrors
  open Newtonsoft.Json
  open RealWorld.Hash

  let jsonToString (json: 'a) = 
    Newtonsoft.Json.JsonConvert.SerializeObject(json)

  let extractStringQueryVal (queryParameters : HttpRequest) name =
    match queryParameters.queryParam name with
    | Choice1Of2 queryVal -> queryVal
    | Choice2Of2 _ -> String.Empty

  let extractNumericQueryVal (queryParameters : HttpRequest) name = 
    match queryParameters.queryParam name with
    | Choice1Of2 limit -> Convert.ToInt32 limit
    | Choice2Of2 _ -> 0
    
  let routeByOptions (queryParameters : HttpRequest) =
    let listArticleOptions = {
      Limit = extractNumericQueryVal queryParameters "limit";
      Tag = extractStringQueryVal queryParameters "tag";
      Author = extractStringQueryVal queryParameters "author";
      Favorited = extractStringQueryVal queryParameters "favorited";
      Offset = extractNumericQueryVal queryParameters "offset";
    }
    (Successful.OK "")
    
  let hashPassword (request: UserRequest) = 
    {request with user = { request.user with hash = RealWorld.Hash.Crypto.fastHash request.user.password } }

  let registerNewUser dbClient = 
    request ( fun inputGraph -> 
      Newtonsoft.Json.JsonConvert.DeserializeObject<UserRequest>(inputGraph.rawForm |> System.Text.ASCIIEncoding.UTF8.GetString)
      |> hashPassword
      |> registerWithBson dbClient 
      |> RealWorld.Convert.userRequestToUser 
      |> jsonToString 
      |> Successful.OK
    )

  let currentUserByEmail dbClient email = 
    (getUser dbClient email).Value
    |> BsonDocConverter.toUser
    |> jsonToString

  let getCurrentUser dbClient httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try  
        return! Successful.OK (currentUserByEmail dbClient token.UserName) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let updateUser dbClient  httpContext = 
     Auth.useToken httpContext (fun token -> async {
      try  
        let user = JsonConvert.DeserializeObject<UserRequest>( httpContext.request.rawForm |> System.Text.ASCIIEncoding.UTF8.GetString)
        
        updateRequestedUser dbClient user |> ignore

        return! Successful.OK (user |> jsonToString) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })
  
  let getUserProfile dbClient username httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try 
        return! Successful.OK (currentUserByEmail dbClient username) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let createNewArticle dbCLient httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try 
        let newArticle = (JsonConvert.DeserializeObject<Article>(httpContext.request.rawForm |> System.Text.Encoding.UTF8.GetString))  
    
        let checkedArticle = 
          newArticle 
          |> RealWorld.Convert.checkNullAuthor 
          |> RealWorld.Convert.checkNullSlug
          |> RealWorld.Convert.checkFavoriteIds
        
        insertNewArticle checkedArticle dbCLient |> ignore
    
        return! Successful.OK (checkedArticle |> jsonToString) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })


  let getArticlesBy slug dbClient =    
    getArticleBySlug dbClient slug
    |> RealWorld.Convert.extractArticleList
    |> jsonToString
    |> Successful.OK

  let defaultTagsIfEmpty = function
    | Some tags -> tags
    | None -> { tags = [||] }

  let defaultArticleIfEmpty = function
    | Some articles -> Array.ofList articles
    | None -> [||]

  let getTagList dbClient =
    getSavedTagList dbClient 
    |> defaultTagsIfEmpty
    |> jsonToString
    |> Successful.OK 

  let getArticles dbClient httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try 
        let articles = 
          getSavedArticles dbClient
          |> RealWorld.BsonDocConverter.toArticleList
          |> jsonToString         

        return! Successful.OK articles httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let getArticlesForFeed dbClient httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try 
        let articles = 
          getSavedFollowedArticles dbClient
          |> defaultArticleIfEmpty
          |> jsonToString           

        return! Successful.OK articles httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })
    

  let addArticleWithSlug json (slug: string) (dbClient: MongoDB.Driver.IMongoDatabase) = 
    let currentArticle = json |> Suave.Json.fromJson<Article> 
    let updatedSlug = { currentArticle.article with slug = slug}
    
    insertNewArticle ({currentArticle with article = updatedSlug }) dbClient
    |> jsonToString
    |> Successful.OK

  let deleteArticleBy slug dbClient httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try  
        deleteArticleBySlug slug dbClient |> ignore
        return! Successful.OK ("") httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })    

  let addCommentBy json slug dbClient = 
    let possibleArticleId = getArticleBySlug dbClient slug
    match possibleArticleId with
    | Some articleId -> 
      saveNewComment (Suave.Json.fromJson<Comment> json) (articleId.Id.ToString()) dbClient |> ignore
      Successful.OK (json |> jsonToString)
    | None -> 
      Successful.OK ({errors = {body = [|"Could not find article by slug"|]}} |> jsonToString) 

  let getCommentsBySlug slug dbClient = 
    getCommentsFromArticlesBySlug slug dbClient
    |> jsonToString
    |> Successful.OK 

  let deleteComment (_, (id: string)) dbCLient = 
    deleteWithCommentId id dbCLient
    |> jsonToString
    |> Successful.OK

  let favoriteArticle slug dbClient httpContext = 
    // TODO: Get the current user, then get the article by the slug and add the object id to the users favorite list
     Auth.useToken httpContext (fun token -> async {
      try  
        favoriteArticleForUser dbClient token.UserName slug |> ignore
        return! Successful.OK ("") httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let removeFavoriteCurrentUser slug dbClient httpContext =     
    Auth.useToken httpContext (fun token -> async {
      try 
        
        removeFavoriteArticleFromUser dbClient token.UserName slug |> ignore
        return! Successful.OK ("") httpContext  
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let getFollowedProfile dbClient username httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try        
        let profile = followUser dbClient token.UserName username |> Convert.userToProfile

        return! Successful.OK (profile |> jsonToString) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })

  let removeFollowedProfile dbClient username httpContext = 
    Auth.useToken httpContext (fun token -> async {
      try 
        let profile = unfollowUser dbClient token.UserName username |> Convert.userToProfile 
        return! Successful.OK (profile |> jsonToString) httpContext
      with ex ->
        return! Suave.RequestErrors.NOT_FOUND "Database not available" httpContext
    })