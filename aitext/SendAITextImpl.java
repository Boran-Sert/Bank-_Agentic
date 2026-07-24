package com.turkiyefinans.actions.atmbranch.assistant.aitext;

import com.turkiyefinans.actions.BaseAction;
import com.turkiyefinans.actions.screen.action.ScreenActionResponse;
import com.turkiyefinans.core.exceptions.MiddlewareException;
import com.turkiyefinans.flow.atmbranch.assistant.TFSendAITextFlow;
import com.turkiyefinans.models.TFCreditCard;
import org.springframework.stereotype.Service;

@Service("sendAITextAction")
public class SendAITextImpl extends BaseAction implements SendAIText {

    public SendAITextImpl() {
        super();
    }

    private TFSendAITextFlow createFlow() throws MiddlewareException {
        TFSendAITextFlow flow = new TFSendAITextFlow();
        flow.setService(getService());
        flow.setAdcIntegrationRestClient(adcIntegrationRestClient);
        flow.setSearchService(searchService);
        flow.setCachedUtil(cachedUtil);
        flow.setMessages(messages);
        flow.setInvoiceManagerRest(invoiceManagerRest);
        flow.setServiceBeanSharer(serviceBeanSharer);
        flow.setSealApi(sealApi);
        flow.setAppReview(showStoreSurvey);
        flow.setAdcIntegrationRestClientAlt(adcIntegrationRestClientAlternative);
        flow.setRkycRestClientService(rkycRestClientService);
        return flow;
    }

    @Override
    public ScreenActionResponse openScreen(String mkCodeValue, String mkCodesJson, String themeValue, String protocolValue) throws MiddlewareException {
        return createFlow().openScreen(mkCodeValue, mkCodesJson, themeValue, protocolValue);
    }

    @Override
    public ScreenActionResponse buildCardDebtPaymentConfirm(TFCreditCard selectedCard, String amount, String selectedAccountNo, String themeValue) throws MiddlewareException {
        return createFlow().buildCardDebtPaymentConfirm(selectedCard, amount, selectedAccountNo, themeValue);
    }
}
