// QxBgmManager 离线播放器 · GIL 提取器
// 从千星地图存储的 .gil 中解析全部节点图与变量(cue 表),输出 JSON 供 qx_player.py 使用。
// 依赖 th08-qianxing 工程内的 genshin-ts(node_modules),路径可用 --genshin-ts 覆盖。
// 用法: node qx_extract.mjs <map.gil> <out.json> [--genshin-ts <dir>]
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

const args = process.argv.slice(2)
const gilPath = args[0]
const outPath = args[1]
const gtIdx = args.indexOf('--genshin-ts')
const gtDir = gtIdx >= 0 ? args[gtIdx + 1] : 'D:/ProgramSource2/th08-qianxing/outputs/th08-bgm/node_modules/genshin-ts'

if (!gilPath || !outPath) {
  console.error('用法: node qx_extract.mjs <map.gil> <out.json> [--genshin-ts <dir>]')
  process.exit(2)
}

// 在 genshin-ts 包目录下解析其依赖(injector 的 protobuf 等)
const requireAt = createRequire(path.join(gtDir, 'package.json'))
const base = path.join(path.dirname(requireAt.resolve('genshin-ts/package.json')), 'dist', 'src', 'injector')
const imp = (n) => import(pathToFileURL(path.join(base, n)).href)
const [binary, nodeGraph, proto] = await Promise.all([imp('binary.js'), imp('node_graph.js'), imp('proto.js')])

const fs = await import('node:fs')
const bytes = fs.readFileSync(gilPath)
if (bytes.length < 24) throw new Error('不是有效的 .gil:文件太小')
const payload = bytes.subarray(20, bytes.length - 4)
const fields = []
const blobs = []
binary.parseMessage(payload, 0, payload.length, 0, 0, 0, 0, 0, 0, 0, fields, { nodeGraphBlobFields: blobs })
const index = nodeGraph.buildNodeGraphFieldIndex(payload, blobs.length ? blobs : fields)
const protoLoader = proto.loadGiaProto()

const pinVal = (v) => {
  if (!v) return null
  if (v.bInt?.val !== undefined) return Number(v.bInt.val)
  if (v.bFloat?.val !== undefined) return Number(v.bFloat.val)
  if (v.bString?.val !== undefined) return String(v.bString.val)
  // 数组常量节点的元素包在 ConcreteBase 里
  const inner = v.bConcreteValue?.value
  if (inner) {
    if (inner.bInt?.val !== undefined) return Number(inner.bInt.val)
    if (inner.bFloat?.val !== undefined) return(Number(inner.bFloat.val))
    if (inner.bString?.val !== undefined) return String(inner.bString.val)
  }
  return null
}

const graphs = []
for (const [id, list] of index) {
  for (const { field } of list) {
    const g = protoLoader.nodeGraphMessage.decode(payload.subarray(field.dataStart, field.dataEnd))
    const vars = {}
    for (const v of g.graphValues || []) {
      const arr = v.values?.bArray?.entries
      if (arr) {
        vars[v.name] = arr.map((e) => pinVal(e)).filter((x) => x !== null)
      } else {
        const single = v.values?.bInt?.val ?? v.values?.bFloat?.val
        if (single !== undefined) vars[v.name] = Number(single)
      }
    }
    graphs.push({
      id: (g.id && g.id.toString) ? g.id.toString() : String(g.id ?? ''),
      name: g.name ?? '',
      nodeCount: (g.nodes || []).length,
      // 节点字面量;数组常量节点(引脚数>=50)额外产出 array 字段(去除首尾控制引脚)
      nodes: (g.nodes || []).map((n) => {
        const vals = (n.pins || []).map((p) => pinVal(p.value)).filter((x) => x !== null)
        const out = {
          nodeIndex: n.nodeIndex,
          nodeId: Number(n.genericId?.nodeId ?? 0),
          values: vals.length <= 20 ? vals : undefined,
        }
        if ((n.pins || []).length >= 50 && typeof vals[0] === 'number' && vals.length > 20) {
          // pin[0]=元素个数,尾部可能有类型引脚:数据 = vals[1 .. 1+vals[0]]
          const count = vals[0]
          out.array = vals.slice(1, 1 + count)
        }
        return out
      }),
      vars,
    })
  }
}
fs.writeFileSync(outPath, JSON.stringify({ file: path.resolve(gilPath), graphs }, null, 1))
console.log(`graphs=${graphs.length} -> ${outPath}`)
for (const g of graphs) {
  const varNames = Object.keys(g.vars).join(',')
  console.log(`  #${g.id} "${g.name}" nodes=${g.nodeCount}${varNames ? ' vars=' + varNames : ''}`)
}
