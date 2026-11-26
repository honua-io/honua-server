[.shards[] | select(.advisory == true) | .name] as $advisory
| .default_shards_when_no_match as $smoke
| $selection
| (.shards - $advisory) as $required
| if (.shards | length) > 0 and ($required | length) == 0 then
    . + {shards: $smoke, run_all: false, reason: "no_path_match"}
  else
    .shards = $required
  end
