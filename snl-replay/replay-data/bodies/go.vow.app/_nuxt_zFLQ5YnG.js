import{aU as e}from"./urfOaDfp.js";function o(r){if(!r)return"";if(r.startsWith("+"))return r;let t=e(r,{regionCode:"US"});return t.valid?t.number.e164:r}export{o as f};
