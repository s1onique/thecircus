module Api exposing (getProduct)

import Http
import Product exposing (Product)


{-| Fetch the product information from the API.
-}
getProduct : (Result Http.Error Product -> msg) -> Cmd msg
getProduct toMsg =
    Http.get
        { url = "/api/v1/about"
        , expect = Http.expectJson toMsg Product.decoder
        }
