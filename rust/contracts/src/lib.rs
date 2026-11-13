//! Generated NPLN contract types and server stubs. Source: `contracts/*.proto`.
//! Nothing here is hand-written; edit the `.proto` files instead.

macro_rules! package {
    ($name:ident, $proto:literal) => {
        pub mod $name {
            include!(concat!(env!("OUT_DIR"), "/", $proto, ".rs"));
        }
    };
}

pub mod nn {
    pub mod npln {
        pub mod auth {
            package!(v1, "nn.npln.auth.v1");
        }
        pub mod matchmaking {
            package!(v1, "nn.npln.matchmaking.v1");
        }
        pub mod gamesync {
            package!(v1, "nn.npln.gamesync.v1");
        }
        pub mod ugcstore {
            package!(v1, "nn.npln.ugcstore.v1");
        }
    }
}
