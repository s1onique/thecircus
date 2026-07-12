module Product exposing (Product, decoder)

import Json.Decode exposing (Decoder, field, string)


{-| The product type representing product identity.
-}
type alias Product =
    { name : String
    , tagline : String
    , description : String
    }


{-| JSON decoder for Product.
-}
decoder : Decoder Product
decoder =
    Json.Decode.map3 Product
        (field "name" string)
        (field "tagline" string)
        (field "description" string)
