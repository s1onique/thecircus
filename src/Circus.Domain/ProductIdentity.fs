namespace Circus.Domain

/// Product name - a non-empty string identifying the product.
type ProductName = private ProductName of string

/// Product tagline - a short descriptive phrase.
type ProductTagline = private ProductTagline of string

/// Product description - a detailed explanation of the product.
type ProductDescription = private ProductDescription of string

/// The canonical product identity for The Circus.
type ProductIdentity =
    { Name: ProductName
      Tagline: ProductTagline
      Description: ProductDescription }

module ProductName =
    /// Extract the string value from a ProductName.
    let value (ProductName name) = name

module ProductTagline =
    /// Extract the string value from a ProductTagline.
    let value (ProductTagline tagline) = tagline

module ProductDescription =
    /// Extract the string value from a ProductDescription.
    let value (ProductDescription description) = description

module ProductIdentity =
    /// The canonical current product identity.
    let current =
        { Name = ProductName "The Circus"
          Tagline = ProductTagline "Team-scale Leamas"
          Description = ProductDescription "The team-scale coordination, evidence, and governance platform for Leamas." }
