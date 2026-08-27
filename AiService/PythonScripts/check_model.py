import onnx
model = onnx.load("intent_model/model.onnx")
onnx.checker.check_model(model)
print("Model is valid!")