package com.turkiyefinans.actions.atmbranch.assistant.aitext;

import com.google.gson.Gson;
import com.pozitron.aesop.annotations.AesopCall;
import com.turkiyefinans.actions.BaseSpringController;
import com.turkiyefinans.actions.atmbranch.assistant.aitext.adc.AdcAIClient;
import com.turkiyefinans.actions.atmbranch.assistant.aitext.adc.AdcAIRequest;
import com.turkiyefinans.actions.atmbranch.assistant.aitext.adc.AdcAIResponse;
import com.turkiyefinans.actions.screen.action.ScreenActionResponse;
import com.turkiyefinans.core.Util;
import com.turkiyefinans.core.exceptions.MiddlewareException;
import com.turkiyefinans.core.messages.Messages;
import com.turkiyefinans.core.utils.LogUtil;
import com.turkiyefinans.models.TFCreditCard;

import jakarta.servlet.http.HttpServletResponse;
import org.datacontract.schemas._2004._07.TurkiyeFinans_MsmqLogger_AdcLog.AdcLogType;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.stereotype.Controller;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.servlet.mvc.method.annotation.StreamingResponseBody;

import java.nio.charset.StandardCharsets;
import java.util.Map;

@Controller
public class SendAITextController extends BaseSpringController {

    private final SendAIText sendAITextAction;
    private final AdcAIClient adcAIClient;
    private final Gson gson = new Gson();

    public SendAITextController(Messages messages, SendAITextValidator sendAITextValidator,
                                @Qualifier("sendAITextAction") SendAIText sendAIText,
                                AdcAIClient adcAIClient) {
        super(messages);
        this.validator = sendAITextValidator;
        this.sendAITextAction = sendAIText;
        this.adcAIClient = adcAIClient;
    }

    @AesopCall(name = "SendAIText")
    @PostMapping("/SendAIText.json")
    public StreamingResponseBody sendAIText(@RequestBody SendAITextRequest request,
                                            HttpServletResponse httpServletResponse) throws MiddlewareException {
        if (request.isPrepare()) {
            return os -> {};
        }

        if (request.getText() == null || request.getText().isEmpty()) {
            throw new MiddlewareException(messages.getMessage("girilen_bilgiler_hatalidir"), AdcLogType.Warning, 480, 9907);
        }

        LogUtil.getLog().info("SendAIText user message: " + request.getText());

        try {
            AdcAIRequest aiRequest = new AdcAIRequest();
            aiRequest.setText(request.getText());
            aiRequest.setThemeValue(request.getThemeValue());
            aiRequest.setSessionToken(Util.getUserSessionToken());

            AdcAIResponse aiResponse = adcAIClient.send(aiRequest);
            LogUtil.getLog().info("SendAIText ADC response type: " + aiResponse.getType());

            return switch (aiResponse.getType()) {
                case OPEN_SCREEN -> {
                    ScreenActionResponse screen = sendAITextAction.openScreen(
                        aiResponse.getMkCodeValue(), aiResponse.getMkCodesJson(),
                        request.getThemeValue(), request.getProtocolValue()
                    );
                    yield writeScreen(httpServletResponse, screen);
                }
                case CONFIRM_PAYMENT -> {
                    TFCreditCard card = new TFCreditCard();
                    card.setCreditCardNumber(aiResponse.getCardNumber());
                    card.setProductTypeName(aiResponse.getCardProductName());
                    ScreenActionResponse confirm = sendAITextAction.buildCardDebtPaymentConfirm(
                        card, aiResponse.getAmount(), aiResponse.getAccountNo(), request.getThemeValue()
                    );
                    yield writeScreen(httpServletResponse, confirm);
                }
                default -> writeText(httpServletResponse, aiResponse.getText());
            };

        } catch (Exception e) {
            LogUtil.getLog().error("SendAIText error: " + e.getMessage(), e);
            throw new MiddlewareException("AI service error: " + e.getMessage(), AdcLogType.Warning, 480, 9908);
        }
    }

    private StreamingResponseBody writeText(HttpServletResponse response, String text) {
        setJsonHeaders(response);
        byte[] json = gson.toJson(Map.of("type", "TEXT", "text", text != null ? text : ""))
                         .getBytes(StandardCharsets.UTF_8);
        return os -> { os.write(json); os.flush(); };
    }

    private StreamingResponseBody writeScreen(HttpServletResponse response, ScreenActionResponse screen) {
        setJsonHeaders(response);
        byte[] json = gson.toJson(screen).getBytes(StandardCharsets.UTF_8);
        return os -> { os.write(json); os.flush(); };
    }

    private void setJsonHeaders(HttpServletResponse response) {
        response.setContentType("application/json; charset=UTF-8");
        response.setHeader("Cache-Control", "no-cache");
    }
}
